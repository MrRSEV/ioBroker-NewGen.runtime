'use strict';

const nodeCrypto = require('node:crypto');

const DEFAULT_SECRET = 'Zgfr56gFe87jJOM';
const AES_PREFIX = '$/aes-192-cbc:';
const isAesKey = key => /^[0-9a-f]{48}$/.test(key);

function xor(key, value) {
  let result = '';
  for (let i = 0; i < value.length; i++) result += String.fromCharCode(key[i % key.length].charCodeAt(0) ^ value.charCodeAt(i));
  return result;
}

/** Entspricht tools.encrypt des js-controllers (AES-192-CBC, sonst XOR-Fallback). */
function encrypt(key, value) {
  if (!isAesKey(key)) return xor(key, value);
  const iv = nodeCrypto.randomBytes(16);
  const cipher = nodeCrypto.createCipheriv('aes-192-cbc', Buffer.from(key, 'hex'), iv);
  const encrypted = Buffer.concat([cipher.update(value), cipher.final()]);
  return `${AES_PREFIX}${iv.toString('hex')}:${encrypted.toString('hex')}`;
}

/** Entspricht tools.decrypt des js-controllers. */
function decrypt(key, value) {
  if (!value.startsWith(AES_PREFIX) || !isAesKey(key)) return xor(key, value);
  const [, ivHex, dataHex] = value.split(':', 3);
  const decipher = nodeCrypto.createDecipheriv('aes-192-cbc', Buffer.from(key, 'hex'), Buffer.from(ivHex, 'hex'));
  return Buffer.concat([decipher.update(Buffer.from(dataHex, 'hex')), decipher.final()]).toString();
}

module.exports = { DEFAULT_SECRET, encrypt, decrypt };
