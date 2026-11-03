# Changelog

All significant changes to this project are documented in this file.

The format follows the principles of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project loosely adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Standalone README, architecture documentation and API reference for the separately maintained Admin UI adapter project.

### Changed
- Custom adapter files in `Adapters/custom` are copied to the build and publish output while preserving their directory structure.

## [0.4.2] - 2026-11-01

### Added
- Legacy (Node.js) adapters can be installed as a ZIP in the Admin UI (tab "Adapter" → "+ Legacy-Adapter (ZIP)", `POST /api/adapters/legacy`, permission `AdapterInstall`) and uninstalled again. The package must be built (`io-package.json`, entry file, `node_modules`); `@iobroker/adapter-core` inside it is replaced by the bundled drop-in (`Legacy/adapter-core`, copied to the output directory). Failures and progress are written to the log.
- Multiple instances of the same adapter (`alexa.0`, `alexa.1`, …), also for legacy adapters: `POST /api/adapters/instances` (`add`/`remove`). Extra custom instances are persisted in `adapter.instances`, legacy instances in `adapter.legacyInstances` and restored at startup; each instance has its own config file. Non-primary custom instances default their state prefix to the instance id. `adapter.legacyAutoStart` only adopts already present packages as `name.0`.
- Admin UI: tab "Instanzen" with "Instanz hinzufügen", start/stop/restart, configuration, uptime and removal per instance (custom and legacy).
- Admin UI: new "Übersicht" start page (`#tab-overview`) with status, adapter/instance/object cards, system information (platform, runtime, uptime, RAM, CPU), active adapters and a system log excerpt, backed by the new `/api/overview` endpoint (permission `SystemRead`).
- Global `[system]` settings in `config.toml` (`temperatureUnit`, `currency`, `dateFormat`, `isFloatComma`, `firstDayOfWeek`, `country`, `city`, `address`, `latitude`, `longitude`, `setupDone`), editable via `/api/config`. After the forced password change the Admin UI shows a first-run setup step ("Wichtige Haupteinstellungen") until `system.setupDone` is true; it can be skipped. Latitude/longitude can be taken from the browser's geolocation.
- Unified per-instance adapter configuration for Custom and Legacy adapters (`Core/Adapters/Configuration`): `AdapterConfigSchema` (defaults + jsonConfig, also read from `io-package.json` / `admin/jsonConfig.json`), `AdapterConfigService` (`AdapterRegistry.Configs`, files in `data/adapter-config/<instanceId>.json`, validation, password masking) and `InstanceConfig` (instance values first, global `config.toml` as fallback).
- `IAdapter.ConfigSchema`; all bundled adapters (Hue, Shelly, KNX, Modbus, Homematic, Bosch, Alexa, FritzBox, MQTT, Zigbee) declare their settings.
- Legacy adapters receive `--configFile <path>`; `Adapter.js` loads it as `this.config`.
- Admin: `POST /api/adapters/config` (`get`/`save`, permission `SystemConfigure`) and a full-page config form (Save / Save and close / Close) that renders jsonConfig. Saving restarts running custom and legacy instances automatically.
- OAuth login for cloud adapters (`OAuthFlowService`, `AdapterRegistry.OAuth`, `POST /api/adapters/oauth`): isolated Chrome/Edge session (temporary profile, private mode), catches the authorization code on `http://localhost:<port>/callback` (PKCE + state check) and stores the tokens in the instance config for now (plaintext, to be replaced by secure storage). The Alexa adapter uses provider `amazon` and refreshes its token automatically.

### Changed
- Admin UI: "States" tab renamed to "Objekte" (`#tab-objects`).
- Autostart entries by adapter name only apply to the primary instance (`.0`).
- Bridge handshake assigns the connection by exact instance id (e.g. `iot.1`) before falling back to the adapter name.
## [0.4.1] - 2026-10-30

### Added
- `Legacy/AdapterCore`: legacy adapter core emulator and drop-in replacement for the npm package `@iobroker/adapter-core` (version 3.2.3), so unmodified legacy adapters run without js-controller. All communication uses the unchanged `NodeAdapterClient` TCP bridge.
  - `Adapter` (EventEmitter, constructor and `class extends` pattern) with `ready`, `stateChange`, `objectChange`, `message` and `unload` events, and the properties `name`, `namespace`, `instance`, `adapterDir`, `connected`, `log`, `config`.
  - State, object and message API (`*Async` promises plus callback variants, `Foreign` variants, `sendTo`, `sendToHost`) mapped to bridge frames (`StateUpdate`, `StateQuery`, `Subscribe`, `ObjectCreate`, `ObjectQuery`, `ObjectExtend`, `ObjectDelete`, `Message`, `HostMessage`, `Log`).
  - `BridgeStateSynchronizer`: central frame handling, automatic reconnect with backoff and subscription replay, write buffering while disconnected, local state/object caches and event de-duplication.
  - Logger forwarding to the bridge with console fallback.
  - `build-package.js` / `npm run pack:package` builds a self-contained, installable tarball (bundles `NodeAdapterClient.js` unchanged).
  - Tests (`npm test`, fake bridge) and an mqtt-like example adapter.
- Documentation: README goals and `Legacy/AdapterCore/README.md` (frame mapping, installation).

### Notes
- `TcpBridgeServer` is unchanged: it does not yet answer query frames; until the host replies (`metadata.reply=true`, same `correlationId`), reads fall back to the local cache after a timeout.
- No TypeScript typings (`.d.ts`) are shipped.

## [0.3.0] - 2026-10-28

### Added
- Central adapter lifecycle logging in `MainRuntimeContext`: every adapter start (`Connected`), real stop (`Connected` → `Disconnected`) and error state (`Faulted`) is written to the log, for both custom C# adapters and legacy Node.js adapters (including restarts after crashes).
- Admin dashboard is now also served at `/` and `/index.html` (in addition to `/admin`).
- Integration tests cover the new dashboard routes.
- Custom adapter runtime with dependency-ordered start/stop, adapter deployment/installer, backup service, adapter migration catalog, release candidate builder, native adapters (MQTT, Zigbee, Hue, Shelly, FritzBox, Bosch, KNX, Modbus, Homematic, Alexa) and the external AdminUI assembly.

### Fixed
- `http://127.0.0.1:8080/` returned `404 Not Found` because the dashboard was only registered at `/admin`.

## [0.2.0] - 2026-10-05

### Hinzugefügt
- **Core/Bridge** – TCP-Bridge & Node.js-Adapter-Runtime (Sprint 2):
  - `BridgeMessageType` (Handshake, HandshakeAck, Frame, Heartbeat, Error) und `BridgeEnvelope` als kompaktes JSON-Transport-Format.
  - `BridgeProtocol` – Framing mit 4-Byte-Längenpräfix über `NetworkStream`, verhindert Nachrichtenfragmentierung.
  - `BridgeConnection` – kapselt eine einzelne TCP-Verbindung, serialisiert Schreibzugriffe.
  - `TcpBridgeServer` – TCP-Listener mit Handshake (Adapter sendet Namen, Host vergibt ID im Format `Name.id`), bidirektionaler Frame-Transport; eingehende Payloads werden 1:1 auf den `SystemMessageBus` gespiegelt.
  - `NodeAdapterRuntime` – verwaltet isolierte Node.js-Adapterprozesse auf Basis von `RSEV.Utilities.Processes` (`NodeProcess`, `RuntimeProcessController`), inklusive automatischem Crash-Recovery (periodischer Health-Check + Neustart) und Heartbeat-Monitoring.
- **Core/Registry** – `AdapterRegistry`, `AdapterRegistration`, `AdapterStatus`:
  - Thread-sichere zentrale Verwaltung aller Adapter (Custom + ioBroker-Legacy).
  - Automatische, eindeutige ID-Vergabe im Format `Name.id` (z. B. `mqtt.0`, `mqtt.1`).
  - Felder: Name, Id, Type, Status, Metadata, TcpEndpoint (ioBroker), InstanceReference (Custom, ab Sprint 3).
- `AdapterRouter` erweitert um `AttachTcpBridge(...)`: `IoBroker`-Frames werden nun tatsächlich über die TCP-Bridge an den adressierten Adapter zugestellt, statt nur geloggt zu werden (Sprint-1-Fallback).
- `MainRuntimeContext` erweitert um `AdapterRegistry`, `TcpBridge`, `NodeRuntime` sowie die Methoden `StartTcpBridgeAsync`, `InitializeNodeRuntime`, `ShutdownBridgeAndNodeRuntimeAsync`.
- `OnStart`/`OnStop` integrieren den Lifecycle von TCP-Bridge und Node.js-Adapter-Runtime (Port aus `config.toml` → `ipc.ipcPort`, Fallback 5001).
- DokuWiki-Dokumentation unter `Doku/` erweitert um `core-bridge.txt`, `core-registry.txt`; `architektur.txt`, `core-routing.txt`, `runtime-lifecycle.txt`, `sprintplan.txt`, `start.txt` aktualisiert.

## [0.1.0] - 2026-10-05

### Hinzugefügt
- Grundgerüst des Runtime-Lifecycles (`FirstRun`, `OnStart`, `OnStop`) auf Basis von RSEV.Utilities.
- `MainRuntimeContext` als applikationsweiter Singleton-`RuntimeContext` inkl. eigenem Logger (`SystemLog`, schreibt `latest.log` in das Basisverzeichnis) und Konfigurationsregister (`ConfConfig`).
- `ConfigCreator` zum automatischen Anlegen einer Standard-`config.toml` beim Erststart.
- `ConfigLoader` zum Einlesen der `config.toml` in das RSEV.Utilities-Konfigurationsregister beim Systemstart, inklusive automatischem Zurücksetzen von `firstrun` auf `false`.
- `UnexpectedUserExitHandler` zur sauberen Behandlung von CTRL+C, Konsolen-Schließen sowie Windows-Shutdown-/Logoff-Events mit garantiertem `OnStop`-Aufruf.
- **Core/Matrix** – Semantische Zustands- und Referenzmatrix (`SemanticStateMatrix`):
  - `MatrixObject`, `StateNode`, `HistorySample` als Kernmodelle des Objektbaums.
  - `IEmbeddingService` / `HashingEmbeddingService` für deterministische semantische Embeddings.
  - `IVectorIndex` / `VectorIndex` für semantische Ähnlichkeitssuche (Kosinus-Similarity).
  - `IRoleService` / `DefaultRoleService` für Lese-/Schreibrechte pro Benutzer und State.
  - Vollständige JSONL-Snapshot-Persistenz (`LoadSnapshotAsync`, `WriteSnapshotAsync`, periodischer `StartCronSnapshot`).
  - API: `RegisterObject`, `GetObject`, `GetState`, `SetState`, `Subscribe`/`Unsubscribe`, `SemanticQuery`, `GetHistory`, `DisplayAsJson`.
- **Core/Bus** – Zentraler neutraler Systembus:
  - `BusFrame`-Record (AdapterId, Address, Payload, ValueType, Kind, Timestamp, Metadata).
  - `SystemMessageBus` (basierend auf `RSEV.Utilities.Messaging.BaseMessageBus`) mit thread-sicherem Publish/Subscribe inkl. Wildcard-Abonnements.
- **Core/Routing** – `AdapterRouter`-Skeleton zur Unterscheidung zwischen internem Routing (`CustomAdapter`) und TCP-Bridge-Routing (`IoBrokerAdapter`, folgt in einer späteren Version).
- Registrierung von `SemanticStateMatrix`, `SystemMessageBus` und `AdapterRouter` im `MainRuntimeContext`, Initialisierung beim Systemstart sowie automatisches Schreiben des finalen Snapshots beim Herunterfahren.

[0.2.0]: https://github.com/MrRSEV/ioBroker-NewGen/releases/tag/dev-0.2.0
[0.1.0]: https://github.com/MrRSEV/ioBroker-NewGen/releases/tag/dev-0.1.0
