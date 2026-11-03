'use strict';

const { FrameKind } = require('./BridgeStateSynchronizer');
const {
  withCallback,
  requireId,
  qualify,
  globToRegExp,
  normalizeState,
  typeOfValue
} = require('./util');

/** State-Methoden; alle Operationen laufen als Frames über den Synchronizer. */
class StateApi {
  constructor(core) {
    this.core = core;
  }

  _own(id) {
    return qualify(this.core.namespace, id);
  }

  async _write(fullId, state, ack, onlyIfChanged) {
    requireId(fullId);
    const { sync, namespace, now } = this.core;
    const prev = sync.states.get(fullId);
    const effectiveAck = ack !== null && typeof ack === 'object' ? undefined : ack;
    const next = normalizeState(state, { ack: effectiveAck, from: `system.adapter.${namespace}`, now: now(), prev });
    if (onlyIfChanged && prev && prev.val === next.val && prev.ack === next.ack) return fullId;
    sync.states.set(fullId, next);
    await sync.send({
      frame: 'StateUpdate',
      kind: FrameKind.StateUpdate,
      address: fullId,
      payload: next,
      valueType: typeOfValue(next.val)
    });
    return fullId;
  }

  async _read(fullId) {
    requireId(fullId);
    const { sync, now } = this.core;
    const reply = await sync.request({ frame: 'StateQuery', kind: FrameKind.Command, address: fullId });
    if (!reply) return sync.states.get(fullId) ?? null;
    if (reply.payload === null || reply.payload === undefined) {
      sync.states.delete(fullId);
      return null;
    }
    const state = normalizeState(reply.payload, { now: now(), prev: sync.states.get(fullId) });
    sync.states.set(fullId, state);
    return state;
  }

  async _readMany(pattern) {
    const { sync, now } = this.core;
    const reply = await sync.request({ frame: 'StateQuery', kind: FrameKind.Command, address: pattern, metadata: { multi: 'true' } });
    const result = {};
    if (reply && reply.payload && typeof reply.payload === 'object') {
      for (const [id, value] of Object.entries(reply.payload)) {
        if (value === null) continue;
        result[id] = normalizeState(value, { now: now(), prev: sync.states.get(id) });
        sync.states.set(id, result[id]);
      }
      return result;
    }
    const regex = globToRegExp(pattern);
    for (const [id, state] of sync.states) {
      if (regex.test(id)) result[id] = state;
    }
    return result;
  }

  async _subscribe(pattern) {
    requireId(pattern);
    const { sync, subs } = this.core;
    if (subs.add('state', pattern)) {
      await sync.send({ frame: 'Subscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'state' }, buffer: false });
    }
  }

  async _unsubscribe(pattern) {
    requireId(pattern);
    const { sync, subs } = this.core;
    if (subs.remove('state', pattern)) {
      await sync.send({ frame: 'Unsubscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'state' }, buffer: false });
    }
  }

  setStateAsync(id, state, ack, options) { return this._write(this._own(id), state, ack); }
  setStateChangedAsync(id, state, ack, options) { return this._write(this._own(id), state, ack, true); }
  setForeignStateAsync(id, state, ack, options) { return this._write(requireId(id), state, ack); }
  setForeignStateChangedAsync(id, state, ack, options) { return this._write(requireId(id), state, ack, true); }
  getStateAsync(id) { return this._read(this._own(id)); }
  getForeignStateAsync(id) { return this._read(requireId(id)); }
  getStatesAsync(pattern) { return this._readMany(qualify(this.core.namespace, pattern)); }
  getForeignStatesAsync(pattern) { return this._readMany(requireId(pattern)); }
  subscribeStatesAsync(pattern) { return this._subscribe(qualify(this.core.namespace, pattern)); }
  unsubscribeStatesAsync(pattern) { return this._unsubscribe(qualify(this.core.namespace, pattern)); }
  subscribeForeignStatesAsync(pattern) { return this._subscribe(requireId(pattern)); }
  unsubscribeForeignStatesAsync(pattern) { return this._unsubscribe(requireId(pattern)); }

  setState(...args) { return withCallback(args, a => this.setStateAsync(...a)); }
  setStateChanged(...args) { return withCallback(args, a => this.setStateChangedAsync(...a)); }
  setForeignState(...args) { return withCallback(args, a => this.setForeignStateAsync(...a)); }
  setForeignStateChanged(...args) { return withCallback(args, a => this.setForeignStateChangedAsync(...a)); }
  getState(...args) { return withCallback(args, a => this.getStateAsync(...a)); }
  getForeignState(...args) { return withCallback(args, a => this.getForeignStateAsync(...a)); }
  getStates(...args) { return withCallback(args, a => this.getStatesAsync(...a)); }
  getForeignStates(...args) { return withCallback(args, a => this.getForeignStatesAsync(...a)); }
  subscribeStates(...args) { return withCallback(args, a => this.subscribeStatesAsync(...a)); }
  unsubscribeStates(...args) { return withCallback(args, a => this.unsubscribeStatesAsync(...a)); }
  subscribeForeignStates(...args) { return withCallback(args, a => this.subscribeForeignStatesAsync(...a)); }
  unsubscribeForeignStates(...args) { return withCallback(args, a => this.unsubscribeForeignStatesAsync(...a)); }
}

module.exports = { StateApi };
