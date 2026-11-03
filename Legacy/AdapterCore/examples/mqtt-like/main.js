'use strict';

const utils = require('@iobroker/adapter-core');

class MqttLikeAdapter extends utils.Adapter {
  constructor(options = {}) {
    super({ ...options, name: 'mqtt-like' });
    this.on('ready', this.onReady.bind(this));
    this.on('stateChange', this.onStateChange.bind(this));
    this.on('message', this.onMessage.bind(this));
    this.on('unload', this.onUnload.bind(this));
  }

  async onReady() {
    await this.setObjectNotExistsAsync('info.connection', {
      type: 'state',
      common: { name: 'Connected', type: 'boolean', role: 'indicator.connected', read: true, write: false },
      native: {}
    });
    await this.setStateAsync('info.connection', true, true);
    this.subscribeStates('*');
    this.log.info('mqtt-like adapter ready');
  }

  onStateChange(id, state) {
    if (state && !state.ack) this.setState(id, { val: state.val, ack: true });
  }

  onMessage(obj) {
    if (obj.command === 'ping' && obj.callback) this.sendTo(obj.from, obj.command, 'pong', obj.callback);
  }

  onUnload(callback) {
    this.setState('info.connection', false, true, () => callback());
  }
}

if (require.main !== module) {
  module.exports = options => new MqttLikeAdapter(options);
} else {
  new MqttLikeAdapter();
}
