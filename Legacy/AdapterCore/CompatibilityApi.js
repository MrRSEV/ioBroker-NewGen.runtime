'use strict';

const { FrameKind } = require('./BridgeStateSynchronizer');

/** Erweiterte NodeAdapterRuntime-Dienste außerhalb des State-Realtime-Pfads. */
class CompatibilityApi {
  constructor(core) {
    this.core = core;
  }

  createWebSocketSession(correlationId, route) {
    return {
      route,
      sendText: text => this._sendWebSocketMessage(correlationId, Buffer.from(String(text ?? ''), 'utf8'), true),
      sendBinary: data => this._sendWebSocketMessage(correlationId, Buffer.isBuffer(data) ? data : Buffer.from(data), false),
      close: () => this.core.sync.send({
        frame: 'WebSocketClose',
        kind: FrameKind.Command,
        address: route,
        metadata: { correlationId }
      })
    };
  }

  async notifyAsync(level, message, payload) {
    await this.core.sync.send({
      frame: 'Notification',
      kind: FrameKind.Event,
      address: this.core.namespace,
      payload,
      metadata: { level: level || 'info', message: message || '' }
    });
  }

  /**
   * Publishes an ioBroker-compatible instance message through the legacy
   * admin adapter. ioBroker.admin handles command `im` and forwards the
   * {m, s, d} tuple to its Socket.IO clients.
   */
  async publishInstanceMessageAsync(messageType, socketId, data, options = {}) {
    const target = options.adminInstance || 'admin.0';
    if (!target || !messageType) return false;
    return this.core.sync.send({
      frame: 'Message',
      kind: FrameKind.Command,
      address: target,
      payload: { m: messageType, s: socketId || '', d: data },
      metadata: { command: 'im', from: this.core.namespace }
    });
  }

  async getMetadataAsync(key) {
    const reply = await this.core.sync.request({
      frame: 'MetadataQuery',
      kind: FrameKind.Command,
      address: key || '',
      metadata: { operation: 'get' }
    });
    return reply ? reply.payload : null;
  }

  async setMetadataAsync(key, value) {
    const reply = await this.core.sync.request({
      frame: 'Metadata',
      kind: FrameKind.Command,
      address: key || '',
      payload: value,
      metadata: { operation: 'set' }
    });
    return reply ? reply.payload : undefined;
  }

  async readFileAsync(relativePath) {
    const reply = await this._fileRequest('read', relativePath);
    if (!reply || !reply.contentBase64) return null;
    return Buffer.from(reply.contentBase64, 'base64');
  }

  async writeFileAsync(relativePath, content) {
    const bytes = Buffer.isBuffer(content) ? content : Buffer.from(String(content ?? ''), 'utf8');
    const reply = await this._fileRequest('write', relativePath, {
      contentBase64: bytes.toString('base64')
    });
    return Boolean(reply && reply.written);
  }

  async deleteFileAsync(relativePath) {
    const reply = await this._fileRequest('delete', relativePath);
    return Boolean(reply && reply.deleted);
  }

  async listFilesAsync() {
    const reply = await this._fileRequest('list', '.');
    return Array.isArray(reply) ? reply : [];
  }

  async registerWebEndpointAsync(route, handler, options = {}) {
    if (typeof handler === 'function') {
      this.core.webEndpoints.set(route, {
        handler,
        kind: String(options.kind || 'http').toLowerCase() === 'websocket' ? 'websocket' : 'http'
      });
    }
    const reply = await this.core.sync.request({
      frame: 'WebEndpoint',
      kind: FrameKind.Command,
      address: route,
      payload: { route },
      metadata: {
        operation: 'register',
        kind: String(options.kind || 'http').toLowerCase() === 'websocket' ? 'websocket' : 'http'
      }
    });
    return Boolean(reply && reply.registered);
  }

  async getProcessStatusAsync() {
    const reply = await this._processRequest('status');
    return reply || { success: false, running: false };
  }

  async restartSelfAsync() {
    const reply = await this._processRequest('restart');
    return Boolean(reply && reply.success);
  }

  async stopSelfAsync() {
    const reply = await this._processRequest('stop');
    return Boolean(reply && reply.success);
  }

  async _fileRequest(operation, relativePath, payload) {
    const reply = await this.core.sync.request({
      frame: 'FileStorage',
      kind: FrameKind.Command,
      address: relativePath,
      payload,
      metadata: { operation }
    });
    return reply ? reply.payload : null;
  }

  async _processRequest(operation) {
    const reply = await this.core.sync.request({
      frame: 'ProcessLifecycle',
      kind: FrameKind.Command,
      address: this.core.namespace,
      metadata: { operation }
    });
    return reply ? reply.payload : null;
  }

  _sendWebSocketMessage(correlationId, data, isText) {
    return this.core.sync.send({
      frame: 'WebSocketMessage',
      kind: FrameKind.Command,
      address: '',
      payload: { dataBase64: data.toString('base64'), isText, endOfMessage: true },
      metadata: { correlationId },
      buffer: false
    });
  }
}

module.exports = { CompatibilityApi };
