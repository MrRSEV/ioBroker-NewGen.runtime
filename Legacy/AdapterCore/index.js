'use strict';

const path = require('path');
const fs = require('fs');
const { Adapter } = require('./Adapter');

const dataDir = () => path.resolve(process.env.NEWGEN_DATA_DIR || path.join(process.cwd(), 'iobroker-data'));

const EXIT_CODES = Object.freeze({
  NO_ERROR: 0,
  JS_CONTROLLER_STOPPED: 1,
  INVALID_ADAPTER_ID: 10,
  UNCAUGHT_EXCEPTION: 11,
  ADAPTER_REQUESTED_TERMINATION: 11,
  START_IMMEDIATELY_AFTER_STOP: 156,
  START_AFTER_UPDATE: 157,
  CANNOT_FIND_ADAPTER_DIR: 20,
  CANNOT_FIND_ADAPTER_VERSION: 21
});

const isDocker = () => {
  try {
    return fs.existsSync('/.dockerenv');
  } catch {
    return false;
  }
};

const adapterCore = {
  Adapter,
  controllerDir: path.resolve(__dirname),
  getControllerDir: () => path.resolve(__dirname),
  getAbsoluteDefaultDataDir: () => `${dataDir()}${path.sep}`,
  getAbsoluteInstanceDataDir: adapter => path.join(dataDir(), adapter.namespace),
  isDocker,
  EXIT_CODES,
  version: require('./package.json').version
};

module.exports = adapterCore;
module.exports.default = adapterCore;
