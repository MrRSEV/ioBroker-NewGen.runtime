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
      now: options.now || Date.now
    };
    this._stateApi = new StateApi(core);
    this._objectApi = new ObjectApi(core);
    this._messageApi = new MessageApi(core);

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
    sync.on('transportError', error => {
      if (!this.connected) this.log.debug(`Bridge: ${error.message}`);
    });
  }

  async _start() {
    if (this._unloaded) return;
    await this._synchronizer.start();
    if (this._unloaded) return;
    this.emit('ready');
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

module.exports = { Adapter };
