'use strict';

const net = require('net');
const { EventEmitter } = require('events');

const MessageType = Object.freeze({
  Handshake: 0,
  HandshakeAck: 1,
  Frame: 2,
  Heartbeat: 3,
  Error: 4
});

/**
 * Minimaler, unabhängiger Node.js-Client für die ioBroker-NewGen-TCP-Bridge.
 * Das Format entspricht BridgeProtocol.cs: 4-Byte-Big-Endian-Länge gefolgt
 * von einem kompakt serialisierten JSON-Envelope.
 */
class NodeAdapterClient extends EventEmitter {
  constructor(options = {}) {
    super();
    this.host = options.host || process.env.NEWGEN_BRIDGE_HOST || '127.0.0.1';
    this.port = Number(options.port || process.env.NEWGEN_BRIDGE_PORT || 5001);
    this.name = options.name || process.env.NEWGEN_ADAPTER_NAME || 'legacy';
    this.heartbeatMs = Number(options.heartbeatMs || process.env.NEWGEN_HEARTBEAT_MS || 10000);
    this.socket = null;
    this.adapterId = null;
    this.buffer = Buffer.alloc(0);
    this.heartbeatTimer = null;
    this.pendingWrites = Promise.resolve();
  }

  async connect() {
    if (this.socket) return this.adapterId;

    this.socket = await new Promise((resolve, reject) => {
      const socket = net.createConnection({ host: this.host, port: this.port });
      const onError = error => {
        socket.removeListener('connect', onConnect);
        reject(error);
      };
      const onConnect = () => {
        socket.removeListener('error', onError);
        resolve(socket);
      };
      socket.once('error', onError);
      socket.once('connect', onConnect);
    });

    this.socket.on('data', chunk => this.#onData(chunk));
    this.socket.on('close', () => this.#onClosed());
    this.socket.on('error', error => this.emit('error', error));

    const ackPromise = this.#waitFor('handshakeAck');
    await this.#write({ type: MessageType.Handshake, adapterId: this.name });
    const ack = await ackPromise;
    this.adapterId = ack.adapterId;
    this.heartbeatTimer = setInterval(() => {
      this.#write({ type: MessageType.Heartbeat, adapterId: this.adapterId }).catch(error => this.emit('error', error));
    }, this.heartbeatMs);
    this.emit('connected', this.adapterId);
    return this.adapterId;
  }

  async sendFrame({ address, payload = Buffer.alloc(0), valueType = 'raw', frameKind = 0, metadata = {} }) {
    if (!this.socket || !this.adapterId) throw new Error('Bridge client is not connected');
    const bytes = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
    return this.#write({
      type: MessageType.Frame,
      adapterId: this.adapterId,
      address,
      valueType,
      frameKind,
      payloadBase64: bytes.toString('base64'),
      metadata,
      timestamp: new Date().toISOString()
    });
  }

  async close() {
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = null;
    if (this.socket) {
      this.socket.end();
      this.socket = null;
    }
    this.adapterId = null;
  }

  #write(envelope) {
    const payload = Buffer.from(JSON.stringify(envelope), 'utf8');
    const packet = Buffer.alloc(4 + payload.length);
    packet.writeUInt32BE(payload.length, 0);
    payload.copy(packet, 4);
    this.pendingWrites = this.pendingWrites.then(() => new Promise((resolve, reject) => {
      if (!this.socket) return reject(new Error('Bridge client is not connected'));
      this.socket.write(packet, error => error ? reject(error) : resolve());
    }));
    return this.pendingWrites;
  }

  #onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32BE(0);
      if (this.buffer.length < length + 4) return;
      const raw = this.buffer.subarray(4, length + 4).toString('utf8');
      this.buffer = this.buffer.subarray(length + 4);
      let envelope;
      try {
        envelope = JSON.parse(raw);
      } catch (error) {
        this.emit('error', error);
        continue;
      }
      if (envelope.type === MessageType.HandshakeAck) this.emit('handshakeAck', envelope);
      else if (envelope.type === MessageType.Frame) this.emit('frame', envelope);
      else if (envelope.type === MessageType.Heartbeat) this.emit('heartbeat', envelope);
      else if (envelope.type === MessageType.Error) this.emit('bridgeError', envelope);
    }
  }

  #waitFor(eventName) {
    return new Promise((resolve, reject) => {
      const onValue = value => { cleanup(); resolve(value); };
      const onError = error => { cleanup(); reject(error); };
      const cleanup = () => {
        this.removeListener(eventName, onValue);
        this.removeListener('error', onError);
      };
      this.once(eventName, onValue);
      this.once('error', onError);
    });
  }

  #onClosed() {
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = null;
    this.emit('disconnected');
  }
}

module.exports = { NodeAdapterClient, MessageType };

if (require.main === module) {
  const argumentsByName = {};
  for (let index = 2; index < process.argv.length; index += 1) {
    const argument = process.argv[index];
    if (argument.startsWith('--') && process.argv[index + 1]) {
      argumentsByName[argument.slice(2)] = process.argv[++index];
    }
  }
  const client = new NodeAdapterClient({
    host: argumentsByName.host,
    port: argumentsByName.port,
    name: argumentsByName.name
  });
  client.connect()
    .then(id => {
      console.log(`connected:${id}`);
      process.once('SIGINT', () => client.close().finally(() => process.exit(0)));
    })
    .catch(error => {
      console.error(error.stack || error);
      process.exitCode = 1;
    });
}
