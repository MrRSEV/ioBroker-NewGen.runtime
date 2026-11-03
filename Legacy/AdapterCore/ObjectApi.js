'use strict';

const { FrameKind } = require('./BridgeStateSynchronizer');
const { withCallback, requireId, qualify, deepMerge, isPlainObject } = require('./util');

/** Objekt-Methoden; Mapping: setObject→ObjectCreate, getObject→ObjectQuery, extendObject→ObjectExtend, delObject→ObjectDelete. */
class ObjectApi {
  constructor(core) {
    this.core = core;
  }

  _own(id) {
    return qualify(this.core.namespace, id);
  }

  async _create(fullId, obj, onlyIfMissing) {
    requireId(fullId);
    if (!isPlainObject(obj)) throw new TypeError('object must be a plain object');
    const { sync } = this.core;
    if (onlyIfMissing && (await this._read(fullId)) !== null) return { id: fullId };
    const stored = { ...obj, _id: fullId };
    sync.objects.set(fullId, stored);
    await sync.send({ frame: 'ObjectCreate', kind: FrameKind.Command, address: fullId, payload: stored });
    return { id: fullId };
  }

  async _read(fullId) {
    requireId(fullId);
    const { sync } = this.core;
    const reply = await sync.request({ frame: 'ObjectQuery', kind: FrameKind.Command, address: fullId });
    if (!reply) return sync.objects.get(fullId) ?? null;
    if (reply.payload === null || reply.payload === undefined) {
      sync.objects.delete(fullId);
      return null;
    }
    sync.objects.set(fullId, reply.payload);
    return reply.payload;
  }

  async _extend(fullId, partial) {
    requireId(fullId);
    if (!isPlainObject(partial)) throw new TypeError('object must be a plain object');
    const { sync } = this.core;
    const merged = deepMerge(sync.objects.get(fullId) || {}, partial);
    merged._id = fullId;
    sync.objects.set(fullId, merged);
    await sync.send({ frame: 'ObjectExtend', kind: FrameKind.Command, address: fullId, payload: partial });
    return { id: fullId, value: merged };
  }

  async _delete(fullId, options) {
    requireId(fullId);
    const { sync } = this.core;
    const recursive = Boolean(options && typeof options === 'object' && options.recursive);
    sync.objects.delete(fullId);
    if (recursive) {
      for (const id of [...sync.objects.keys()]) {
        if (id.startsWith(`${fullId}.`)) sync.objects.delete(id);
      }
    }
    await sync.send({ frame: 'ObjectDelete', kind: FrameKind.Command, address: fullId, metadata: { recursive } });
  }

  async _subscribe(pattern) {
    requireId(pattern);
    const { sync, subs } = this.core;
    if (subs.add('object', pattern)) {
      await sync.send({ frame: 'Subscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'object' }, buffer: false });
    }
  }

  async _unsubscribe(pattern) {
    requireId(pattern);
    const { sync, subs } = this.core;
    if (subs.remove('object', pattern)) {
      await sync.send({ frame: 'Unsubscribe', kind: FrameKind.Command, address: pattern, metadata: { target: 'object' }, buffer: false });
    }
  }

  setObjectAsync(id, obj) { return this._create(this._own(id), obj, false); }
  setObjectNotExistsAsync(id, obj) { return this._create(this._own(id), obj, true); }
  setForeignObjectAsync(id, obj) { return this._create(requireId(id), obj, false); }
  getObjectAsync(id) { return this._read(this._own(id)); }
  getForeignObjectAsync(id) { return this._read(requireId(id)); }
  extendObjectAsync(id, obj) { return this._extend(this._own(id), obj); }
  extendForeignObjectAsync(id, obj) { return this._extend(requireId(id), obj); }
  delObjectAsync(id, options) { return this._delete(this._own(id), options); }
  delForeignObjectAsync(id, options) { return this._delete(requireId(id), options); }
  subscribeObjectsAsync(pattern) { return this._subscribe(qualify(this.core.namespace, pattern)); }
  unsubscribeObjectsAsync(pattern) { return this._unsubscribe(qualify(this.core.namespace, pattern)); }
  subscribeForeignObjectsAsync(pattern) { return this._subscribe(requireId(pattern)); }
  unsubscribeForeignObjectsAsync(pattern) { return this._unsubscribe(requireId(pattern)); }

  setObject(...args) { return withCallback(args, a => this.setObjectAsync(...a)); }
  setObjectNotExists(...args) { return withCallback(args, a => this.setObjectNotExistsAsync(...a)); }
  setForeignObject(...args) { return withCallback(args, a => this.setForeignObjectAsync(...a)); }
  getObject(...args) { return withCallback(args, a => this.getObjectAsync(...a)); }
  getForeignObject(...args) { return withCallback(args, a => this.getForeignObjectAsync(...a)); }
  extendObject(...args) { return withCallback(args, a => this.extendObjectAsync(...a)); }
  extendForeignObject(...args) { return withCallback(args, a => this.extendForeignObjectAsync(...a)); }
  delObject(...args) { return withCallback(args, a => this.delObjectAsync(...a)); }
  delForeignObject(...args) { return withCallback(args, a => this.delForeignObjectAsync(...a)); }
  subscribeObjects(...args) { return withCallback(args, a => this.subscribeObjectsAsync(...a)); }
  unsubscribeObjects(...args) { return withCallback(args, a => this.unsubscribeObjectsAsync(...a)); }
  subscribeForeignObjects(...args) { return withCallback(args, a => this.subscribeForeignObjectsAsync(...a)); }
  unsubscribeForeignObjects(...args) { return withCallback(args, a => this.unsubscribeForeignObjectsAsync(...a)); }
}

module.exports = { ObjectApi };
