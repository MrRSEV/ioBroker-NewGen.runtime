'use strict';

const { EventEmitter } = require('events');
const {
  decodePayload,
  encodePayload,
  normalizeState,
  stateSignature,
  stringifyValues
} = require('./util');

const FrameKind = Object.freeze({ Raw: 0, StateUpdate: 1, Command: 2, Event: 3 });

// Installiertes Paket: gebündelte Kopie in ./vendor; Repository: Legacy/NodeAdapterClient.js.
function loadClientModule() {
  try {
    return require('./vendor/NodeAdapterClient');
  } catch (error) {
    if (error.code !== 'MODULE_NOT_FOUND') throw error;
    return require('../NodeAdapterClient');
  }
}

/**
 * Einzige Stelle, die den NodeAdapterClient benutzt: Verbindungsaufbau,
 * automatischer Reconnect, zentrale Frame-Verarbeitung, Request/Reply-
 * Korrelation sowie lokale State-/Objekt-Caches mit Event-Deduplizierung.
 * Heartbeats verwaltet ausschließlich der NodeAdapterClient.
 *
 * Events: 'connected', 'disconnected', 'stateChange'(id, state),
 * 'objectChange'(id, obj), 'message'(obj), 'frame'(envelope), 'transportError'(err).
 */
class BridgeStateSynchronizer extends EventEmitter {
  constructor(options) {
    super();
    this.namespace = options.namespace;
    this.subscriptions = options.subscriptions;
    this.now = options.now || Date.now;
    this._clientFactory = options.clientFactory || (() => {
      const { NodeAdapterClient } = loadClientModule();
      return new NodeAdapterClient({
        host: options.host,
        port: options.port,
        name: options.clientName || this.namespace,
        heartbeatMs: options.heartbeatMs
      });
    });
    this.requestTimeoutMs = options.requestTimeoutMs ?? 2000;
    this.reconnectMinMs = options.reconnectMinMs ?? 500;
    this.reconnectMaxMs = options.reconnectMaxMs ?? 10000;
    this.handshakeTimeoutMs = options.handshakeTimeoutMs ?? 5000;
    this.maxQueue = options.maxQueue ?? 1000;

    this.states = new Map();
    this.objects = new Map();

    this._client = null;
    this._connecting = false;
    this._stopped = true;
    this._attempts = 0;
    this._reconnectTimer = null;
    this._sequence = 0;
    this._pending = new Map();
    this._queue = [];
    this._stateSignatures = new Map();
    this._objectSignatures = new Map();
    this._seenMessages = new Set();
    this._firstConnection = null;
  }

  get connected() {
    return this._client !== null;
  }

  nextId() {
    this._sequence += 1;
    return `${this.namespace}#${this._sequence}`;
  }

  /** Startet die Verbindung; erfüllt sich bei der ersten erfolgreichen Verbindung. */
  start() {
    if (!this._stopped) return this._firstConnection;
    this._stopped = false;
    this._firstConnection = new Promise(resolve => this.once('connected', resolve));
    this._attempt();
    return this._firstConnection;
  }

  async stop() {
    this._stopped = true;
    if (this._reconnectTimer) clearTimeout(this._reconnectTimer);
    this._reconnectTimer = null;
    for (const entry of this._pending.values()) {
      clearTimeout(entry.timer);
      entry.resolve(null);
    }
    this._pending.clear();
    const client = this._client;
    this._client = null;
    if (client) await this._closeQuietly(client);
  }

  /**
   * Sendet ein Frame. Ohne Verbindung wird es (falls `buffer`) gepuffert und
   * nach dem Reconnect gesendet.
   * @returns {Promise<boolean>} true, wenn direkt übertragen.
   */
  async send(spec) {
    if (this._client) {
      try {
        await this._transmit(this._client, spec);
        return true;
      } catch (error) {
        this.emit('transportError', error);
      }
    }
    if (spec.buffer !== false && this._queue.length < this.maxQueue) this._queue.push(spec);
    return false;
  }

  /** Sendet ein Log-Frame ohne Pufferung. @returns {boolean} false → Konsolen-Fallback. */
  sendLog(level, message) {
    if (!this._client) return false;
    this._transmit(this._client, {
      frame: 'Log',
      kind: FrameKind.Event,
      address: this.namespace,
      payload: { level, message },
      metadata: { level }
    }).catch(error => this.emit('transportError', error));
    return true;
  }

  /**
   * Sendet eine Anfrage und wartet auf ein Reply-Frame (metadata.reply = 'true',
   * gleiche correlationId). @returns {Promise<{payload, metadata}|null>} null
   * bei Timeout, fehlender Verbindung oder Sendefehler.
   */
  async request(spec, { timeoutMs = this.requestTimeoutMs } = {}) {
    if (!this._client) return null;
    const correlationId = this.nextId();
    const reply = new Promise(resolve => {
      const timer = setTimeout(() => {
        this._pending.delete(correlationId);
        resolve(null);
      }, timeoutMs);
      this._pending.set(correlationId, { resolve, timer });
    });
    const sent = await this.send({
      ...spec,
      buffer: false,
      metadata: { ...spec.metadata, correlationId, expectReply: 'true' }
    });
    if (!sent) {
      const entry = this._pending.get(correlationId);
      if (entry) {
        clearTimeout(entry.timer);
        this._pending.delete(correlationId);
        entry.resolve(null);
      }
    }
    return reply;
  }

  _transmit(client, spec) {
    return client.sendFrame({
      address: spec.address,
      payload: encodePayload(spec.payload),
      valueType: spec.valueType || 'json',
      frameKind: spec.kind ?? FrameKind.Raw,
      metadata: stringifyValues({ ...spec.metadata, frame: spec.frame, namespace: this.namespace })
    });
  }

  async _attempt() {
    if (this._stopped || this._connecting || this._client) return;
    this._connecting = true;
    const client = this._clientFactory();
    client.on('error', error => this.emit('transportError', error));
    client.on('bridgeError', envelope => this.emit('transportError', new Error(`Bridge error: ${JSON.stringify(envelope)}`)));
    client.on('frame', envelope => this._onFrame(envelope));
    client.on('disconnected', () => this._onDisconnected(client));

    let timer;
    try {
      const timeout = new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('Bridge handshake timed out')), this.handshakeTimeoutMs);
      });
      await Promise.race([client.connect(), timeout]);
    } catch (error) {
      this._connecting = false;
      this.emit('transportError', error);
      await this._closeQuietly(client);
      this._scheduleReconnect();
      return;
    } finally {
      clearTimeout(timer);
    }

    this._connecting = false;
    if (this._stopped) {
      await this._closeQuietly(client);
      return;
    }
    this._client = client;
    this._attempts = 0;
    await this._replay();
    this.emit('connected');
  }

  async _replay() {
    for (const pattern of this.subscriptions.list('state')) {
      await this.send({ frame: 'Subscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'state' }, buffer: false });
    }
    for (const pattern of this.subscriptions.list('object')) {
      await this.send({ frame: 'Subscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'object' }, buffer: false });
    }
    const queued = this._queue;
    this._queue = [];
    for (const spec of queued) await this.send(spec);
  }

  _onDisconnected(client) {
    if (client !== this._client) return;
    this._client = null;
    this._closeQuietly(client);
    this.emit('disconnected');
    this._scheduleReconnect();
  }

  _scheduleReconnect() {
    if (this._stopped || this._reconnectTimer) return;
    const delay = Math.min(this.reconnectMaxMs, this.reconnectMinMs * 2 ** this._attempts);
    this._attempts += 1;
    this._reconnectTimer = setTimeout(() => {
      this._reconnectTimer = null;
      this._attempt();
    }, delay);
  }

  async _closeQuietly(client) {
    try {
      await client.close();
    } catch {
      // bereits geschlossen
    }
  }

  _onFrame(envelope) {
    const metadata = envelope.metadata || {};
    const payload = decodePayload(envelope.payloadBase64);

    const pending = metadata.reply === 'true' ? this._pending.get(metadata.correlationId) : undefined;
    if (pending) {
      clearTimeout(pending.timer);
      this._pending.delete(metadata.correlationId);
      pending.resolve({ payload, metadata });
      return;
    }

    const frame = String(metadata.frame || '').toLowerCase();
    if (frame === 'message' || frame === 'hostmessage') this._onMessage(envelope, metadata, payload);
    else if (frame === 'objectchange') this._onObject(envelope.address, payload);
    else if (frame === 'statechange' || frame === 'stateupdate' || (!frame && envelope.frameKind === FrameKind.StateUpdate)) {
      this._onState(envelope.address, payload);
    } else this.emit('frame', envelope);
  }

  _onState(id, payload) {
    const state = payload === null || payload === undefined
      ? null
      : normalizeState(payload, { now: this.now(), prev: this.states.get(id) });
    const signature = stateSignature(state);
    if (this._stateSignatures.get(id) === signature) return;
    this._stateSignatures.set(id, signature);
    if (state === null) this.states.delete(id);
    else this.states.set(id, state);
    this.emit('stateChange', id, state);
  }

  _onObject(id, payload) {
    const object = payload === undefined ? null : payload;
    const signature = JSON.stringify(object);
    if (this._objectSignatures.get(id) === signature) return;
    this._objectSignatures.set(id, signature);
    if (object === null) this.objects.delete(id);
    else this.objects.set(id, object);
    this.emit('objectChange', id, object);
  }

  _onMessage(envelope, metadata, payload) {
    const messageId = metadata.messageId || (metadata.expectReply === 'true' ? metadata.correlationId : undefined);
    if (messageId) {
      if (this._seenMessages.has(messageId)) return;
      this._seenMessages.add(messageId);
      if (this._seenMessages.size > 500) this._seenMessages.delete(this._seenMessages.values().next().value);
    }
    const message = {
      command: metadata.command,
      message: payload,
      from: metadata.from || envelope.adapterId,
      _id: metadata.messageId !== undefined && !Number.isNaN(Number(metadata.messageId)) ? Number(metadata.messageId) : undefined
    };
    if (metadata.expectReply === 'true') {
      message.callback = { message: payload, id: metadata.correlationId, ack: false, time: this.now() };
    }
    this.emit('message', message);
  }
}

module.exports = { BridgeStateSynchronizer, FrameKind };
