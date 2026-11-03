'use strict';

const LEVELS = ['silly', 'debug', 'info', 'warn', 'error'];

/** adapter.log: leitet an die Bridge weiter, bei fehlender Verbindung auf die Konsole. */
class LoggerApi {
  constructor({ namespace, sink, level = 'info' }) {
    this.namespace = namespace;
    this._sink = sink;
    this.level = LEVELS.includes(level) ? level : 'info';
    for (const name of LEVELS) {
      this[name] = message => this._log(name, message);
    }
  }

  _log(level, message) {
    if (LEVELS.indexOf(level) < LEVELS.indexOf(this.level)) return;
    const text = typeof message === 'string' ? message : message instanceof Error ? message.stack || message.message : JSON.stringify(message);
    if (this._sink && this._sink(level, text)) return;
    const line = `${level}: ${this.namespace} ${text}`;
    if (level === 'error') console.error(line);
    else if (level === 'warn') console.warn(line);
    else console.log(line);
  }
}

module.exports = { LoggerApi, LEVELS };
