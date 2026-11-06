'use strict';

const fs = require('node:fs');
const path = require('node:path');
const nodeCrypto = require('node:crypto');
const tools = require('./tools');

/** Entspricht password() des js-controllers (PBKDF2-SHA256). */
function password(pw) {
  return {
    hash(salt, iterations, callback) {
      salt = salt || nodeCrypto.randomBytes(16).toString('hex');
      iterations = iterations || 10000;
      nodeCrypto.pbkdf2(pw, salt, iterations, 256, 'sha256', (err, key) => {
        if (err) return callback(err);
        callback(null, `pbkdf2$${iterations}$${key.toString('hex')}$${salt}`);
      });
    },
    check(hashedPassword, callback) {
      if (!hashedPassword) return callback(null, false);
      const key = hashedPassword.split('$');
      if (key.length !== 4 || !key[2] || !key[3]) return callback(new Error('Hash not formatted correctly'));
      if (key[0] !== 'pbkdf2') return callback(new Error('Unknown'));
      this.hash(key[3], parseInt(key[1], 10), (error, newHash) => (error ? callback(error) : callback(null, newHash === hashedPassword)));
    },
    complexity(value, callback) {
      const result = typeof value === 'string' && value.length >= 8 && /\d/.test(value) && /[a-z]/.test(value) && /[A-Z]/.test(value);
      if (typeof callback === 'function') callback(result);
      return result;
    }
  };
}

function isDocker() {
  try {
    return fs.existsSync('/.dockerenv');
  } catch {
    return false;
  }
}

/** Sucht den Adapter-Ordner relativ zum aufrufenden Adapter (node_modules/iobroker.<name>) bzw. im Legacy-Verzeichnis. */
function getAdapterDir(adapter) {
  const names = [`iobroker.${adapter}`, adapter];
  const roots = [];
  let current = process.cwd();
  for (let i = 0; i < 6; i++) {
    roots.push(current, path.join(current, 'node_modules'));
    const parent = path.dirname(current);
    if (parent === current) break;
    current = parent;
  }
  for (const root of roots) {
    for (const name of names) {
      const candidate = path.join(root, name);
      if (fs.existsSync(path.join(candidate, 'io-package.json'))) return candidate;
    }
  }
  return null;
}

async function getInstalledInfo() {
  return {};
}

function unsupported(name) {
  return () => {
    throw new Error(`${name} wird von der ioBroker-NewGen-Laufzeit nicht unterstützt`);
  };
}

module.exports = {
  pattern2RegEx: tools.pattern2RegEx,
  isLocalAddress: tools.isLocalAddress,
  isListenAllAddress: tools.isListenAllAddress,
  isDocker,
  getAdapterDir,
  getInstalledInfo,
  getLocalAddress: () => '127.0.0.1',
  getListenAllAddress: () => '0.0.0.0',
  password,
  session: unsupported('commonTools.session'),
  zipFiles: unsupported('commonTools.zipFiles')
};
