# Legacy AdapterCore (`@iobroker/adapter-core`-Shim)

> **Inoffizielles Community-Projekt.** Dieses Projekt ist kein offizielles ioBroker-Projekt und steht nicht mit dem offiziellen ioBroker-Projekt in Verbindung.

Emuliert `require('@iobroker/adapter-core')` ohne js-controller. Die gesamte Kommunikation läuft
über den unveränderten [`NodeAdapterClient`](../NodeAdapterClient.js) (TCP-Bridge, `Frame`-Nachrichten).
Der Adapter enthält keine eigene TCP-Logik; Heartbeats verwaltet der Client.

## Einbindung

Als eigenständiges npm-Paket (Drop-in-Ersatz, Version 3.2.3, kein Adapter-Code ändert sich):

```
cd Legacy\AdapterCore
npm run pack:package        # erzeugt Legacy/dist/adapter-core/iobroker-adapter-core-3.2.3.tgz
npm install <pfad>\iobroker-adapter-core-3.2.3.tgz     # im Adapter-Verzeichnis
```

Alternativ in der `package.json` des Adapters: `"@iobroker/adapter-core": "file:<pfad>\\iobroker-adapter-core-3.2.3.tgz"`
(oder `overrides`, falls Abhängigkeiten den Core transitiv beziehen). `NodeAdapterClient.js` wird beim Build unverändert nach `vendor/` kopiert.
Exporte wie im Original: `Adapter`, `controllerDir`, `getControllerDir`, `getAbsoluteDefaultDataDir`, `getAbsoluteInstanceDataDir`, `isDocker`, `EXIT_CODES`, `version`, `default`
(Datenverzeichnis über `NEWGEN_DATA_DIR`).

```js
const utils = require('@iobroker/adapter-core');
module.exports = options => new utils.Adapter(options);
```

Auch `class X extends utils.Adapter` wird unterstützt. Der Start (`ready`) erfolgt im nächsten Tick.
Von `NodeAdapterRuntime` übergebene Argumente `--host --port --name [--instance]` werden automatisch gelesen
(alternativ `NEWGEN_BRIDGE_HOST/PORT`). Die Adapter-Einstellungen (`this.config`, entspricht `native`) liest der Core aus der per
`--configFile <pfad>` übergebenen JSON-Datei der Instanz; Fallback ist `NEWGEN_ADAPTER_CONFIG` als JSON. Der Host legt die Datei unter
`data/adapter-config/<name>.<n>.json` an (Defaults aus `io-package.json → native`), bearbeitet wird sie in der Admin-UI unter „Adapter → Konfiguration“.
Änderungen wirken nach einem Neustart des Adapters.

## Dateien

| Datei | Aufgabe |
|---|---|
| `Adapter.js` | Adapter (EventEmitter): Properties, Events, Lifecycle, Unload |
| `StateApi.js` / `ObjectApi.js` / `MessageApi.js` | `*Async`-Methoden (Promises) + Callback-Varianten |
| `LoggerApi.js` | `log.silly/debug/info/warn/error` → Bridge, Fallback Konsole |
| `SubscriptionManager.js` | Wildcard-Subscriptions (`*`) für States/Objekte |
| `BridgeStateSynchronizer.js` | Client, Auto-Reconnect (Backoff 500 ms … 10 s), zentrale Frame-Verarbeitung, Caches, Dedupe |
| `util.js` | Hilfsfunktionen (Callback-Muster, State-Normalisierung) |

Unterstützt: set/get/getStates/subscribe/unsubscribe (State, `Foreign`-Varianten, `setStateChanged`), set/get/extend/del-Object
(`Foreign`, `setObjectNotExists`, `subscribeObjects`), `sendTo`, `sendToHost` (+ `sendToAsync`), Events `ready`, `stateChange`,
`objectChange`, `message`, `unload` (mit Callback, 5 s Timeout).

## Frame-Mapping

Frames nutzen `frameKind` aus `BusFrameKind` (1 = StateUpdate, 2 = Command, 3 = Event); die Operation steht in `metadata.frame`.
`address` ist die State-/Objekt-ID, Muster bzw. Zielinstanz; `payloadBase64` enthält JSON.

| Adapter-Aufruf | `metadata.frame` | Kind |
|---|---|---|
| setState | `StateUpdate` | 1 |
| getState / getStates | `StateQuery` | 2 |
| subscribe/unsubscribe States/Objects | `Subscribe` / `Unsubscribe` (`target=state\|object`) | 2 |
| setObject | `ObjectCreate` | 2 |
| getObject | `ObjectQuery` | 2 |
| extendObject | `ObjectExtend` | 2 |
| delObject | `ObjectDelete` | 2 |
| sendTo / sendToHost | `Message` / `HostMessage` (`command`, `from`) | 2 |
| log | `Log` | 3 |

Anfragen tragen `metadata.correlationId` und `expectReply=true`. Antworten der Bridge/Matrix sind Frames mit
`metadata.reply=true` und gleicher `correlationId`. Ohne Antwort (Timeout 2 s, Standard) bzw. ohne Verbindung wird aus dem lokalen Cache gelesen.
Eingehend verarbeitet: `frame` = `StateChange`/`StateUpdate` (bzw. `frameKind` 1 ohne `frame`), `ObjectChange`, `Message`/`HostMessage`.
State-, Objekt- und Message-Events werden dedupliziert (State: val/ack/ts/lc/q; Message: `messageId`/`correlationId`).
Während einer Trennung werden Schreib-Frames gepuffert (max. 1000) und nach dem Reconnect gesendet; Subscriptions werden erneut registriert.

> Hinweis: Der aktuelle `TcpBridgeServer` veröffentlicht eingehende Frames nur auf den Systembus und beantwortet keine Queries.
> Antwort-/Push-Frames im oben beschriebenen Format müssen vom Host (z. B. Matrix-Anbindung) gesendet werden; bis dahin greift der lokale Cache.

## Tests

`npm test` (Node ≥ 18; Fake-Bridge im Test, `examples/mqtt-like` als unveränderter mqtt-artiger Adapter).
