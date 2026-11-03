'use strict';

const { FrameKind } = require('./BridgeStateSynchronizer');
const { requireId } = require('./util');

const REPLY_TIMEOUT_MS = 30000;

/** sendTo → Message-Frame, sendToHost → HostMessage-Frame, Antworten → Message-Frame mit reply=true. */
class MessageApi {
  constructor(core) {
    this.core = core;
  }

  async _dispatch(frame, target, command, message, callback, timeoutMs = REPLY_TIMEOUT_MS) {
    requireId(target);
    requireId(command);
    const { sync, namespace } = this.core;
    const spec = {
      frame,
      kind: FrameKind.Command,
      address: target,
      payload: message,
      metadata: { command, from: namespace }
    };

    if (callback && typeof callback === 'object') {
      await sync.send({ ...spec, metadata: { ...spec.metadata, reply: 'true', correlationId: callback.id } });
      return undefined;
    }
    if (typeof callback === 'function' || callback === true) {
      const reply = await sync.request(spec, { timeoutMs });
      return reply ? reply.payload : undefined;
    }
    await sync.send(spec);
    return undefined;
  }

  _fireAndHandle(frame, target, command, message, callback) {
    const promise = this._dispatch(frame, target, command, message, callback);
    promise.then(
      result => {
        if (typeof callback === 'function' && result !== undefined) callback(result);
      },
      error => this.core.log.error(`${frame} failed: ${error.message}`)
    );
  }

  sendToAsync(instance, command, message, options = {}) {
    return this._dispatch('Message', instance, command, message, true, options.timeout);
  }

  sendToHostAsync(host, command, message, options = {}) {
    return this._dispatch('HostMessage', host, command, message, true, options.timeout);
  }

  sendTo(instance, command, message, callback) {
    if (typeof message === 'function') {
      callback = message;
      message = undefined;
    }
    this._fireAndHandle('Message', instance, command, message, callback);
  }

  sendToHost(host, command, message, callback) {
    if (typeof message === 'function') {
      callback = message;
      message = undefined;
    }
    this._fireAndHandle('HostMessage', host, command, message, callback);
  }
}

module.exports = { MessageApi };
