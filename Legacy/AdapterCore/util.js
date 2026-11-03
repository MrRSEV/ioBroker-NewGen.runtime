'use strict';

/**
 * Wertet ein optionales Callback-Muster aus: Ist das letzte Argument eine
 * Funktion, wird sie mit (err, result) aufgerufen und nichts zurückgegeben
 * (wie im js-controller). Andernfalls wird das Promise geliefert.
 */
function withCallback(args, operation) {
  const list = [...args];
  const callback = typeof list[list.length - 1] === 'function' ? list.pop() : null;
  const promise = Promise.resolve().then(() => operation(list));
  if (!callback) return promise;
  promise.then(
    result => setImmediate(callback, null, result),
    error => setImmediate(callback, error)
  );
  return undefined;
}

function requireId(id) {
  if (typeof id !== 'string' || id.length === 0) throw new TypeError('id must be a non-empty string');
  return id;
}

function qualify(namespace, id) {
  requireId(id);
  return id === namespace || id.startsWith(`${namespace}.`) ? id : `${namespace}.${id}`;
}

function globToRegExp(pattern) {
  const source = String(pattern).replace(/[.+?^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*');
  return new RegExp(`^${source}$`);
}

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function deepMerge(target, source) {
  const result = isPlainObject(target) ? { ...target } : {};
  for (const [key, value] of Object.entries(source || {})) {
    result[key] = isPlainObject(value) && isPlainObject(result[key]) ? deepMerge(result[key], value) : value;
  }
  return result;
}

function typeOfValue(value) {
  if (typeof value === 'boolean') return 'bool';
  if (typeof value === 'number') return 'double';
  if (typeof value === 'string') return 'string';
  return 'json';
}

/** Erzeugt einen vollständigen ioBroker-State ({val, ack, ts, q, from, lc}). */
function normalizeState(input, { ack, from, now, prev } = {}) {
  const source = isPlainObject(input) && 'val' in input ? input : { val: input };
  const ts = source.ts ?? now ?? Date.now();
  const state = {
    val: source.val === undefined ? null : source.val,
    ack: Boolean(source.ack ?? ack ?? false),
    ts,
    q: source.q ?? 0,
    from: source.from ?? from
  };
  state.lc = source.lc ?? (prev && prev.val === state.val && prev.lc !== undefined ? prev.lc : ts);
  if (source.expire !== undefined) state.expire = source.expire;
  return state;
}

function stateSignature(state) {
  return state === null ? 'null' : JSON.stringify([state.val, state.ack, state.ts, state.lc, state.q]);
}

function encodePayload(value) {
  return value === undefined ? Buffer.alloc(0) : Buffer.from(JSON.stringify(value), 'utf8');
}

function decodePayload(base64) {
  if (!base64) return undefined;
  const text = Buffer.from(base64, 'base64').toString('utf8');
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function stringifyValues(values) {
  const result = {};
  for (const [key, value] of Object.entries(values || {})) {
    if (value !== undefined && value !== null) result[key] = String(value);
  }
  return result;
}

module.exports = {
  withCallback,
  requireId,
  qualify,
  globToRegExp,
  isPlainObject,
  deepMerge,
  typeOfValue,
  normalizeState,
  stateSignature,
  encodePayload,
  decodePayload,
  stringifyValues
};
