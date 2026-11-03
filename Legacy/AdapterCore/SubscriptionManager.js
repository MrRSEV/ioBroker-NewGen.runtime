'use strict';

const { globToRegExp } = require('./util');

/** Verwaltet State- und Objekt-Subscriptions (Wildcard-Muster mit `*`). */
class SubscriptionManager {
  constructor() {
    this._patterns = { state: new Map(), object: new Map() };
  }

  /** @returns {boolean} true, wenn das Muster neu hinzugefügt wurde. */
  add(kind, pattern) {
    const map = this._patterns[kind];
    if (map.has(pattern)) return false;
    map.set(pattern, globToRegExp(pattern));
    return true;
  }

  /** @returns {boolean} true, wenn das Muster existierte. */
  remove(kind, pattern) {
    return this._patterns[kind].delete(pattern);
  }

  matches(kind, id) {
    for (const regex of this._patterns[kind].values()) {
      if (regex.test(id)) return true;
    }
    return false;
  }

  list(kind) {
    return [...this._patterns[kind].keys()];
  }

  clear() {
    this._patterns.state.clear();
    this._patterns.object.clear();
  }
}

module.exports = { SubscriptionManager };
