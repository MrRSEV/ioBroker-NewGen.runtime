'use strict';

const test = require('node:test');
const assert = require('node:assert');
const net = require('net');
const path = require('path');
const Module = require('module');

// Legacy-Adapter benutzen require('@iobroker/adapter-core') → auf diesen Ordner umleiten.
const originalResolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
  if (request === '@iobroker/adapter-core') request = path.resolve(__dirname, '..');
  return originalResolve.call(this, request, ...rest);
};

const utils = require('@iobroker/adapter-core');

class FakeBridge {
  constructor() {
    this.frames = [];
    this.handshakes = 0;
    this.sockets = new Set();
    this.matrix = new Map();
    this.objects = new Map();
    this.server = net.createServer(socket => this._accept(socket));
  }

  listen() {
    return new Promise(resolve => this.server.listen(0, '127.0.0.1', () => resolve(this.server.address().port)));
  }

  _accept(socket) {
    this.sockets.add(socket);
    socket.on('error', () => {});
    socket.on('close', () => this.sockets.delete(socket));
    let buffer = Buffer.alloc(0);
    socket.on('data', chunk => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 4 && buffer.length >= buffer.readUInt32BE(0) + 4) {
        const length = buffer.readUInt32BE(0);
        const envelope = JSON.parse(buffer.subarray(4, 4 + length).toString('utf8'));
        buffer = buffer.subarray(4 + length);
        this._handle(socket, envelope);
      }
    });
  }

  _write(socket, envelope) {
    const payload = Buffer.from(JSON.stringify(envelope));
    const packet = Buffer.alloc(4 + payload.length);
    packet.writeUInt32BE(payload.length, 0);
    payload.copy(packet, 4);
    socket.write(packet);
  }

  _handle(socket, envelope) {
    if (envelope.type === 0) {
      this.handshakes += 1;
      this._write(socket, { type: 1, adapterId: envelope.adapterId });
    } else if (envelope.type === 2) {
      this.frames.push(envelope);
      const meta = envelope.metadata;
      if (meta.frame === 'StateUpdate') {
        this.matrix.set(envelope.address, JSON.parse(Buffer.from(envelope.payloadBase64, 'base64').toString()));
      } else if (meta.frame === 'StateQuery') {
        this.reply(socket, meta, this.matrix.get(envelope.address) ?? null);
      } else if (meta.frame === 'ObjectQuery') {
        this.reply(socket, meta, this.objects.get(envelope.address) ?? null);
      }
    }
  }

  reply(socket, meta, value) {
    this._write(socket, {
      type: 2,
      adapterId: 'bridge',
      address: 'reply',
      payloadBase64: Buffer.from(JSON.stringify(value)).toString('base64'),
      metadata: { reply: 'true', correlationId: meta.correlationId }
    });
  }

  push(metadata, address, value, frameKind = 1) {
    const envelope = {
      type: 2,
      adapterId: 'bridge',
      address,
      valueType: 'json',
      frameKind,
      payloadBase64: Buffer.from(JSON.stringify(value)).toString('base64'),
      metadata
    };
    for (const socket of this.sockets) this._write(socket, envelope);
  }

  dropConnections() {
    for (const socket of this.sockets) socket.destroy();
  }

  find(frame, address) {
    return this.frames.filter(f => f.metadata.frame === frame && (!address || f.address === address));
  }

  close() {
    this.dropConnections();
    return new Promise(resolve => this.server.close(resolve));
  }
}

async function until(predicate, timeoutMs = 3000) {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) throw new Error('timeout waiting for condition');
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

async function setup(options = {}) {
  const bridge = new FakeBridge();
  const port = await bridge.listen();
  const adapter = new utils.Adapter({
    name: 'demo',
    host: '127.0.0.1',
    port,
    handleSignals: false,
    reconnectMinMs: 50,
    requestTimeoutMs: 500,
    ...options
  });
  const ready = new Promise(resolve => adapter.once('ready', resolve));
  await ready;
  const teardown = async () => {
    await adapter._shutdown();
    await bridge.close();
  };
  return { bridge, adapter, teardown };
}

test('starts without js-controller and fires ready', async () => {
  const { adapter, teardown } = await setup();
  assert.strictEqual(adapter.namespace, 'demo.0');
  assert.strictEqual(adapter.connected, true);
  assert.ok(adapter.log && adapter.config && adapter.adapterDir);
  await teardown();
});

test('setStateAsync emits StateUpdate frame, getStateAsync reads from matrix', async () => {
  const { bridge, adapter, teardown } = await setup();
  await adapter.setStateAsync('temp', { val: 21.5, ack: true });
  await until(() => bridge.find('StateUpdate', 'demo.0.temp').length === 1);
  assert.strictEqual(bridge.find('StateUpdate')[0].frameKind, 1);

  bridge.matrix.set('demo.0.hum', { val: 40, ack: true, ts: 1, lc: 1, q: 0 });
  const state = await adapter.getStateAsync('hum');
  assert.strictEqual(state.val, 40);
  assert.strictEqual((await adapter.getForeignStateAsync('demo.0.temp')).val, 21.5);

  const states = await new Promise((resolve, reject) => adapter.getStates('*', (e, r) => (e ? reject(e) : resolve(r))));
  assert.strictEqual(states['demo.0.temp'].val, 21.5);
  await teardown();
});

test('subscribeStates produces deduplicated stateChange events', async () => {
  const { bridge, adapter, teardown } = await setup();
  const events = [];
  adapter.on('stateChange', (id, state) => events.push([id, state && state.val]));
  await adapter.subscribeStatesAsync('*');
  await until(() => bridge.find('Subscribe', 'demo.0.*').length === 1);

  const state = { val: 1, ack: true, ts: 100, lc: 100, q: 0 };
  bridge.push({ frame: 'StateChange' }, 'demo.0.a', state);
  bridge.push({ frame: 'StateChange' }, 'demo.0.a', state);
  bridge.push({ frame: 'StateChange' }, 'other.0.x', state);
  bridge.push({ frame: 'StateChange' }, 'demo.0.a', { ...state, val: 2, ts: 101, lc: 101 });
  await until(() => events.length >= 2);
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.deepStrictEqual(events, [['demo.0.a', 1], ['demo.0.a', 2]]);
  assert.strictEqual(adapter._synchronizer.states.get('demo.0.a').val, 2);
  await teardown();
});

test('objects: create, extend, query, delete and objectChange', async () => {
  const { bridge, adapter, teardown } = await setup();
  await adapter.setObjectAsync('dev', { type: 'device', common: { name: 'x' }, native: {} });
  await adapter.extendObjectAsync('dev', { common: { role: 'r' } });
  bridge.objects.set('demo.0.dev', { _id: 'demo.0.dev', type: 'device' });
  assert.strictEqual((await adapter.getObjectAsync('dev'))._id, 'demo.0.dev');
  assert.strictEqual(await adapter.getForeignObjectAsync('nope.0.x'), null);
  await adapter.delObjectAsync('dev');
  await until(() => bridge.find('ObjectDelete').length === 1);
  assert.ok(bridge.find('ObjectCreate').length === 1 && bridge.find('ObjectExtend').length === 1);

  const changes = [];
  adapter.on('objectChange', (id, obj) => changes.push([id, obj && obj.type]));
  await adapter.subscribeObjectsAsync('*');
  bridge.push({ frame: 'ObjectChange' }, 'demo.0.new', { type: 'state' });
  bridge.push({ frame: 'ObjectChange' }, 'demo.0.new', { type: 'state' });
  await until(() => changes.length >= 1);
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.deepStrictEqual(changes, [['demo.0.new', 'state']]);
  await teardown();
});

test('sendTo / sendToHost create frames, incoming messages emit message events and replies', async () => {
  const { bridge, adapter, teardown } = await setup();
  adapter.sendTo('telegram.0', 'send', { text: 'hi' });
  adapter.sendToHost('system.host.pc', 'getInfo', {});
  await until(() => bridge.find('Message', 'telegram.0').length === 1 && bridge.find('HostMessage').length === 1);

  const received = [];
  adapter.on('message', obj => {
    received.push(obj);
    adapter.sendTo(obj.from, obj.command, 'pong', obj.callback);
  });
  const meta = { frame: 'Message', command: 'ping', from: 'system.adapter.admin.0', expectReply: 'true', correlationId: 'admin#1' };
  bridge.push(meta, 'demo.0', 'hello', 2);
  bridge.push(meta, 'demo.0', 'hello', 2);
  await until(() => bridge.frames.some(f => f.metadata.reply === 'true'));
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.strictEqual(received.length, 1);
  assert.strictEqual(received[0].command, 'ping');
  const answer = bridge.frames.find(f => f.metadata.reply === 'true');
  assert.strictEqual(answer.metadata.correlationId, 'admin#1');
  await teardown();
});

test('automatic reconnect replays subscriptions', async () => {
  const { bridge, adapter, teardown } = await setup();
  await adapter.subscribeStatesAsync('*');
  await until(() => bridge.find('Subscribe').length === 1);
  bridge.dropConnections();
  await until(() => bridge.handshakes === 2 && adapter.connected);
  await until(() => bridge.find('Subscribe').length === 2);
  await adapter.setStateAsync('after', 1);
  await until(() => bridge.find('StateUpdate', 'demo.0.after').length === 1);
  await teardown();
});

test('logger forwards to bridge and falls back to console', async () => {
  const { bridge, adapter, teardown } = await setup();
  adapter.log.info('hello');
  await until(() => bridge.find('Log').length === 1);
  await teardown();

  const lines = [];
  const original = console.log;
  console.log = line => lines.push(line);
  try {
    adapter.log.info('offline');
  } finally {
    console.log = original;
  }
  assert.ok(lines.some(line => line.includes('offline')));
});

test('mqtt-like example adapter starts unchanged', async () => {
  const bridge = new FakeBridge();
  const port = await bridge.listen();
  const factory = require('../examples/mqtt-like/main.js');
  const adapter = factory({ host: '127.0.0.1', port, handleSignals: false, requestTimeoutMs: 300 });
  await until(() => bridge.find('ObjectCreate', 'mqtt-like.0.info.connection').length === 1);
  await until(() => bridge.find('StateUpdate', 'mqtt-like.0.info.connection').length === 1);
  await until(() => bridge.find('Subscribe').length === 1);
  await adapter._shutdown();
  await bridge.close();
});
