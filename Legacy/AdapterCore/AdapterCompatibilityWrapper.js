'use strict';

/**
 * Compatibility layer for adapters written against newer js-controller and
 * @iobroker/adapter-core contracts. Keep this in one place so individual
 * legacy adapters do not need adapter-specific patches.
 */
function ensureLogger(adapter) {
  const current = adapter.log;
  const logger = current && (typeof current === 'object' || typeof current === 'function')
    ? current
    : {};

  for (const level of ['silly', 'debug', 'info', 'warn', 'error']) {
    const method = logger[level];
    if (typeof method === 'function') {
      // Current adapters explicitly call this.log.<level>.bind(this.log).
      // Binding here also keeps the method safe when it is passed as a callback.
      logger[level] = method.bind(logger);
    } else {
      logger[level] = message => {
        const text = typeof message === 'string' ? message : String(message ?? '');
        if (level === 'error') console.error(text);
        else if (level === 'warn') console.warn(text);
        else console.log(text);
      };
    }
  }

  adapter.log = logger;
}

function alias(target, name, fallback) {
  if (typeof target[name] !== 'function' && typeof fallback === 'function') {
    target[name] = fallback;
  }
}

function installFallback(target, name, implementation) {
  if (typeof target[name] !== 'function') {
    target[name] = implementation;
  }
}

/** Normalize the subset of the js-controller adapter contract used by modern legacy adapters. */
function ensureAdapterCompatibility(adapter) {
  ensureLogger(adapter);

  // Object API aliases used by recent adapters.
  alias(adapter, 'getForeignObjectAsync', adapter.getObjectAsync);
  alias(adapter, 'getForeignObjectsAsync', adapter.getObjectsAsync);
  alias(adapter, 'getObjectViewAsync', adapter.getObjectView);
  alias(adapter, 'setForeignObjectAsync', adapter.setObjectAsync);
  alias(adapter, 'setForeignObjectNotExistsAsync', adapter.setObjectNotExistsAsync);
  alias(adapter, 'delForeignObjectAsync', adapter.delObjectAsync);

  // State API aliases used by recent adapters.
  alias(adapter, 'getForeignStateAsync', adapter.getStateAsync);
  alias(adapter, 'getForeignStatesAsync', adapter.getStatesAsync);
  alias(adapter, 'setForeignStateAsync', adapter.setStateAsync);
  alias(adapter, 'setForeignStateChangedAsync', adapter.setStateChangedAsync);
  alias(adapter, 'subscribeForeignStatesAsync', adapter.subscribeStatesAsync);
  alias(adapter, 'unsubscribeForeignStatesAsync', adapter.unsubscribeStatesAsync);

  // Message API aliases used by current adapter-core releases.
  alias(adapter, 'sendToHostAsync', adapter.sendToAsync);

  // The iot remote module binds these adapter-core methods during construction,
  // even when remote access is disabled. Keep every bound function callable.
  // File operations are routed through the already installed CompatibilityApi.
  installFallback(adapter, 'delForeignStateAsync', async id => {
    if (!id) return undefined;
    await adapter.setForeignStateAsync(id, null, true);
    return undefined;
  });
  installFallback(adapter, 'fileExistsAsync', async path => {
    if (typeof adapter.readFileAsync !== 'function') return false;
    return (await adapter.readFileAsync(path)) !== null;
  });
  installFallback(adapter, 'chownFileAsync', async () => true);
  installFallback(adapter, 'chmodFileAsync', async () => true);
  installFallback(adapter, 'unlinkAsync', async path => {
    if (typeof adapter.deleteFileAsync !== 'function') return false;
    return adapter.deleteFileAsync(path);
  });
  installFallback(adapter, 'mkdirAsync', async () => true);
  installFallback(adapter, 'renameAsync', async () => false);
  installFallback(adapter, 'readDirAsync', async () => {
    if (typeof adapter.listFilesAsync !== 'function') return [];
    return adapter.listFilesAsync();
  });
  installFallback(adapter, 'setPasswordAsync', async () => false);

  return adapter;
}

module.exports = { ensureAdapterCompatibility };
