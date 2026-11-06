'use strict';

const path = require('path');
const fs = require('fs');
const { Adapter } = require('./Adapter');
const { EXIT_CODES: REAL_EXIT_CODES } = require('./exitCodes');
const commonTools = require('./commonTools');
const I18n = require('./i18n');
const TokenRefresher = require('./TokenRefresher');
const Credentials = require('./credentials');

const dataDir = () => path.resolve(process.env.NEWGEN_DATA_DIR || path.join(process.cwd(), 'iobroker-data'));

const EXIT_CODES = Object.freeze({ ...REAL_EXIT_CODES });

const isDocker = commonTools.isDocker;

function getConfig() {
  const file = process.env.NEWGEN_IOBROKER_JSON || path.join(dataDir(), 'iobroker.json');
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return { system: { memoryLimitMB: 0, hostname: '', instanceStartInterval: 2000, compact: false, allowShellCommands: false, memLimitWarn: 0, memLimitError: 0 } };
  }
}

const adapterCore = {
  Adapter,
  adapter: Adapter,
  controllerDir: path.resolve(__dirname),
  getControllerDir: () => path.resolve(__dirname),
  getConfig,
  getAbsoluteDefaultDataDir: () => `${dataDir()}${path.sep}`,
  getAbsoluteInstanceDataDir: adapter => path.join(dataDir(), typeof adapter === 'string' ? adapter : adapter.namespace),
  isDocker,
  commonTools,
  I18n,
  TokenRefresher,
  Credentials,
  EXIT_CODES,
  version: require('./package.json').version
};

module.exports = adapterCore;
module.exports.default = adapterCore;
