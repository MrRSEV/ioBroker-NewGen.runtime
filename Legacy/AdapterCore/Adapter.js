'use strict';

const { EventEmitter } = require('events');
const path = require('path');
const fs = require('fs');
const { BridgeStateSynchronizer } = require('./BridgeStateSynchronizer');
const { SubscriptionManager } = require('./SubscriptionManager');
const { StateApi } = require('./StateApi');
const { ObjectApi } = require('./ObjectApi');
const { MessageApi } = require('./MessageApi');
const { LoggerApi } = require('./LoggerApi');
const { CompatibilityApi } = require('./CompatibilityApi');
const { decrypt, encrypt, DEFAULT_SECRET } = require('./crypto');

const UNLOAD_TIMEOUT_MS = 5000;

function readArguments(argv) {
  const result = {};
  for (let index = 2; index < argv.length; index += 1) {
    if (argv[index].startsWith('--') && argv[index + 1] !== undefined) result[argv[index].slice(2)] = argv[++index];
  }
  return result;
}

function readConfigFile(file) {
  if (!file) return undefined;
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return undefined;
  }
}

function parseConfig(value) {
  if (!value) return {};
  try {
    return JSON.parse(value);
  } catch {
    return {};
  }
}

/**
 * Legacy-kompatibler Adapter ohne js-controller. Unterstützt sowohl
 * `new utils.Adapter({ name, ready, stateChange, ... })` als auch
 * `class MyAdapter extends utils.Adapter`. Der Start erfolgt automatisch im
 * nächsten Tick, sodass Event-Handler im Konstruktor noch registriert werden können.
 */
class Adapter extends EventEmitter {
  constructor(options = {}) {
    super();
    if (typeof options === 'string') options = { name: options };
    const args = readArguments(process.argv);

    this.name = options.name || args.name || path.basename(process.cwd());
    const instanceFromName = /\.(\d+)$/.exec(this.name);
    if (instanceFromName) this.name = this.name.slice(0, instanceFromName.index);
    this.instance = Number(options.instance ?? args.instance ?? (instanceFromName ? instanceFromName[1] : 0)) || 0;
    this.namespace = `${this.name}.${this.instance}`;
    this.adapterDir = options.adapterDir || (require.main && require.main.filename ? path.dirname(require.main.filename) : process.cwd());
    this.config = options.config || readConfigFile(args.configFile) || parseConfig(process.env.NEWGEN_ADAPTER_CONFIG);
    this.common = options.common || {};
    this.connected = false;
    this._unloaded = false;
    this._timers = new Set();

    const subscriptions = new SubscriptionManager();
    this._synchronizer = new BridgeStateSynchronizer({
      namespace: this.namespace,
      subscriptions,
      host: options.host || args.host,
      port: options.port || args.port,
      clientFactory: options.clientFactory,
      heartbeatMs: options.heartbeatMs,
      requestTimeoutMs: options.requestTimeoutMs,
      reconnectMinMs: options.reconnectMinMs,
      reconnectMaxMs: options.reconnectMaxMs,
      handshakeTimeoutMs: options.handshakeTimeoutMs,
      now: options.now
    });

    this.log = new LoggerApi({
      namespace: this.namespace,
      level: options.logLevel || process.env.NEWGEN_LOG_LEVEL || 'info',
      sink: (level, message) => this._synchronizer.sendLog(level, message)
    });

    const core = {
      namespace: this.namespace,
      sync: this._synchronizer,
      subs: subscriptions,
      log: this.log,
      now: options.now || Date.now,
      webEndpoints: new Map(),
      webSockets: new Map()
    };
    this._stateApi = new StateApi(core);
    this._objectApi = new ObjectApi(core);
    this._messageApi = new MessageApi(core);
    this._compatibilityApi = new CompatibilityApi(core);

    for (const event of ['ready', 'stateChange', 'objectChange', 'message', 'unload']) {
      if (typeof options[event] === 'function') this.on(event, options[event]);
    }
    this._wireSynchronizer(subscriptions);

    if (options.handleSignals !== false) {
      for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => this.terminate(0));
    }
    if (options.autoStart !== false) setImmediate(() => this._start());
  }

  _wireSynchronizer(subscriptions) {
    const sync = this._synchronizer;
    sync.on('connected', () => {
      this.connected = true;
    });
    sync.on('disconnected', () => {
      this.connected = false;
      this.log.warn('Bridge connection lost, reconnecting');
    });
    sync.on('stateChange', (id, state) => {
      if (subscriptions.matches('state', id)) this.emit('stateChange', id, state);
    });
    sync.on('objectChange', (id, obj) => {
      if (subscriptions.matches('object', id)) this.emit('objectChange', id, obj);
    });
    sync.on('message', message => this.emit('message', message));
    sync.on('webEndpointRequest', (envelope, payload) => this._handleWebEndpointRequest(envelope, payload));
    sync.on('webSocketOpen', (envelope, payload) => this._handleWebSocketOpen(envelope, payload));
    sync.on('webSocketMessage', (envelope, payload) => this._handleWebSocketMessage(envelope, payload));
    sync.on('webSocketClose', (envelope, payload) => this._handleWebSocketClose(envelope, payload));
    sync.on('transportError', error => {
      if (!this.connected) this.log.debug(`Bridge: ${error.message}`);
    });
  }

  async _handleWebEndpointRequest(envelope, payload) {
    const route = payload && payload.route ? payload.route : envelope.address;
    const registration = this._compatibilityApi.core.webEndpoints.get(route);
    const handler = registration && registration.kind === 'http' ? registration.handler : null;
    let response = { statusCode: handler ? 200 : 404, headers: {}, body: Buffer.alloc(0) };
    if (handler) {
      try {
        const result = await handler({
          route,
          headers: (payload && payload.headers) || {},
          body: payload && payload.bodyBase64 ? Buffer.from(payload.bodyBase64, 'base64') : Buffer.alloc(0)
        });
        if (result && typeof result === 'object' && ('statusCode' in result || 'body' in result || 'headers' in result)) {
          response = {
            statusCode: Number(result.statusCode || 200),
            headers: result.headers || {},
            body: Buffer.isBuffer(result.body) ? result.body : Buffer.from(String(result.body ?? ''), 'utf8')
          };
        } else {
          response.body = Buffer.isBuffer(result) ? result : Buffer.from(String(result ?? ''), 'utf8');
        }
      } catch (error) {
        response = { statusCode: 500, headers: {}, body: Buffer.from(error.message || 'web endpoint failed', 'utf8') };
      }
    }

    await this._synchronizer.send({
      frame: 'WebEndpointResponse',
      kind: 2,
      address: route,
      payload: {
        statusCode: response.statusCode,
        headers: response.headers,
        bodyBase64: response.body.toString('base64')
      },
      metadata: { reply: 'true', correlationId: envelope.metadata && envelope.metadata.correlationId }
    });
  }

  async _handleWebSocketOpen(envelope, payload) {
    const correlationId = envelope.metadata && envelope.metadata.correlationId;
    const route = payload && payload.route ? payload.route : envelope.address;
    const registration = this._compatibilityApi.core.webEndpoints.get(route);
    const handler = registration && registration.kind === 'websocket' ? registration.handler : null;
    const session = this._compatibilityApi.createWebSocketSession(correlationId, route);
    this._compatibilityApi.core.webSockets.set(correlationId, { handler, session });
    let accepted = Boolean(handler);
    if (handler) {
      try {
        if (typeof handler === 'function') await handler({ type: 'open', route, headers: (payload && payload.headers) || {}, session });
      } catch (error) {
        accepted = false;
        this.log.error(`WebSocket endpoint failed: ${error.message}`);
      }
    }
    await this._synchronizer.send({
      frame: 'WebSocketOpenResponse',
      kind: 2,
      address: route,
      payload: { statusCode: accepted ? 101 : 404 },
      metadata: { reply: 'true', correlationId }
    });
    if (!accepted) this._compatibilityApi.core.webSockets.delete(correlationId);
  }

  async _handleWebSocketMessage(envelope, payload) {
    const correlationId = envelope.metadata && envelope.metadata.correlationId;
    const registration = this._compatibilityApi.core.webSockets.get(correlationId);
    if (!registration || !registration.handler) return;
    const data = payload && payload.dataBase64 ? Buffer.from(payload.dataBase64, 'base64') : Buffer.alloc(0);
    try {
      await registration.handler({
        type: 'message',
        data,
        isText: Boolean(payload && payload.isText),
        session: registration.session
      });
    } catch (error) {
      this.log.error(`WebSocket message handler failed: ${error.message}`);
    }
  }

  async _handleWebSocketClose(envelope) {
    const correlationId = envelope.metadata && envelope.metadata.correlationId;
    const registration = this._compatibilityApi.core.webSockets.get(correlationId);
    if (registration && registration.handler) {
      try {
        await registration.handler({ type: 'close', session: registration.session });
      } catch (error) {
        this.log.error(`WebSocket close handler failed: ${error.message}`);
      }
    }
    this._compatibilityApi.core.webSockets.delete(correlationId);
  }

  async _start() {
    if (this._unloaded) return;
    await this._synchronizer.start();
    if (this._unloaded) return;
    this._secret = await this._objectApi._getSecret();
    if (this._unloaded) return;
    this.emit('ready');
  }

  /** decrypt(value) nutzt das System-Secret, decrypt(secret, value) das angegebene. */
  decrypt(secretOrValue, value) {
    return value === undefined ? decrypt(this._secret || DEFAULT_SECRET, secretOrValue) : decrypt(secretOrValue, value);
  }

  encrypt(secretOrValue, value) {
    return value === undefined ? encrypt(this._secret || DEFAULT_SECRET, secretOrValue) : encrypt(secretOrValue, value);
  }

  /** Führt 'unload'-Handler aus (mit Callback, Timeout 5 s) und trennt die Bridge. */
  async _shutdown() {
    if (this._unloaded) return;
    this._unloaded = true;
    if (this.listenerCount('unload') > 0) {
      await new Promise(resolve => {
        const timer = setTimeout(resolve, UNLOAD_TIMEOUT_MS);
        this.emit('unload', () => {
          clearTimeout(timer);
          resolve();
        });
      });
    }
    for (const timer of this._timers) clearTimeout(timer);
    this._timers.clear();
    await this._synchronizer.stop();
    this.connected = false;
  }

  async terminate(code = 0) {
    try {
      await this._shutdown();
    } finally {
      process.exit(code);
    }
  }

  setTimeout(callback, ms, ...args) {
    const timer = setTimeout(() => {
      this._timers.delete(timer);
      callback(...args);
    }, ms);
    this._timers.add(timer);
    return timer;
  }

  clearTimeout(timer) {
    clearTimeout(timer);
    this._timers.delete(timer);
  }
}

function installApi(target, property, apiClass) {
  for (const name of Object.getOwnPropertyNames(apiClass.prototype)) {
    if (name === 'constructor' || name.startsWith('_')) continue;
    Object.defineProperty(target.prototype, name, {
      configurable: true,
      writable: true,
      value(...args) {
        return this[property][name](...args);
      }
    });
  }
}

installApi(Adapter, '_stateApi', StateApi);
installApi(Adapter, '_objectApi', ObjectApi);
installApi(Adapter, '_messageApi', MessageApi);
installApi(Adapter, '_compatibilityApi', CompatibilityApi);

module.exports = { Adapter };
