# Changelog

All significant changes to this project are documented in this file.

The format follows the principles of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project loosely adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [2.0.0-beta] - 2026-11-05

### Added
- Legacy-Socket.IO-Kompatibilität: `publishInstanceMessageAsync(...)` sendet
  Admin-Instanznachrichten im originalen `im`-Format `{m, s, d}` über die
  TCP-Bridge; `sendTo(admin.0, "im", ...)` bleibt ebenfalls nutzbar.

### Changed
- Web-/WebSocket-Endpunkte sind vom `RuntimeApiHost` entkoppelt. Neuer
  webserver-unabhängiger `IWebEndpointRegistry`-Vertrag und separates natives
  Web-Adapterprojekt unter `Adapters/ioBroker-NewGen.web` bzw.
  [github.com/MrRSEV/ioBroker-NewGen.web](https://github.com/MrRSEV/ioBroker-NewGen.web).
  Native und Legacy-Adapter registrieren dort ihre Endpoints; der externe
  Web-Adapter besitzt den Listener.
- Direkter Zugriff installierter Adapter auf `RuntimeApiHost`/
  `WebHostService.cs` wird mit `[RuntimeApiHost]` geloggt. Nur die betroffene
  Adapterinstanz wird isoliert beendet und als `Faulted` markiert; Core,
  RuntimeApiHost, Web-Adapter und andere Adapter bleiben aktiv.
- `AdminUI.dll` registriert seine bestehenden HTTP-Routen nun über
  `IWebEndpointRegistry`, hängt vom `web`-Adapter ab und besitzt keinen eigenen
  Listener mehr. Dadurch bleibt die optionale DLL mit der Schutzregel nutzbar.
- Die Standardkonfiguration aktiviert den optional installierbaren `web`-
  Adapter und dokumentiert `web.enabled`, `web.listenHost` sowie `web.port`.
- `NodeAdapterRuntime.Api` ergänzt Kompatibilitätsdienste für fortgeschrittene
  Legacy-Adapter: `sendTo`, State-/Objekt-Subscriptions, Notifications,
  Metadaten, adapter-isolierter File Storage, zentral delegierte Web-Endpunkte
  und Prozess-Lifecycle. Die TCP-Bridge beantwortet jetzt die zugehörigen
  Query-/Command-Frames und pusht State-/Objektänderungen an Subscriptions.
  Legacy-Adapter erstellen keinen eigenen Webserver; Web-Endpunkte werden an
  `IWebEndpointRegistry` delegiert. Siehe [doc/core-bridge.md](doc/core-bridge.md).
- Semantische Embeddings sind vom Realtime-State-/History-Pfad entkoppelt.
  `SetState()` und StateUpdate-Frames erzeugen keine Embeddings mehr und
  aktualisieren den VectorIndex nicht mehr synchron.
- `MatrixObject` besitzt nun `Embedding`, `LastEmbeddingUpdate` und
  `SemanticDirty`; `StateNode` und `HistorySample` enthalten keine Embeddings
  mehr.
- Neuer `SemanticEmbeddingWorker` mit 30-Sekunden-Intervall verarbeitet
  Registrierungen, semantische Objektänderungen und explizite Reindex-Anfragen
  im Hintergrund.
- Snapshot-Persistenz trennt Objekt- und History-Zeilen. Alte History-
  Embeddings werden beim Laden toleriert, aber nicht in History übernommen.
- Inkrementeller VectorIndex-Updatepfad und `RebuildVectorIndex()` ergänzt.
- `AdminUI.dll` ist kein Bestandteil des Core-Builds und kein Solution-Projekt
  mehr. Die DLL kann bei Bedarf manuell unter `Adapters/native` abgelegt werden;
  der Core kopiert und lädt vorhandene externe Adapter weiterhin generisch.
  Der `ReleaseCandidateBuilder` übernimmt eine separat gebaute AdminUI.dll
  automatisch in Release-Artefakte, wenn sie im Workspace vorhanden ist.
- Dokumentation: [doc/semantic-embeddings.md](doc/semantic-embeddings.md) und
  aktualisierte [doc/core-matrix.md](doc/core-matrix.md).

## [2.0.0] - In progress

This major version is currently under development; further changes will be added before release. The command implementation is documented in [Doku/core-commands.md](Doku/core-commands.md).

### Added
- `core-shutdown` / `core-restart` commands (`-shutdown` / `-restart` on the CLI): graceful core stop/restart strictly via `OnStop.OnStopAsync()`, never by killing the PID. Restart uses the new optional `node.restartCommand`, systemd exit code 75 or a self-spawn; it is skipped if OnStop did not finish cleanly. SIGTERM now also triggers the graceful shutdown, and a closed stdin no longer busy-loops the console reader.
- Command infrastructure: `CoreCommandHost` (console and CLI), `CommandPipeServer`/`CommandPipeClient` (local named pipe / Unix socket, current user only). `ioBroker-NewGen.exe -stop <id>`, `-unload <id>`, `-cmd <name> …` or `-<name> …` send a command to the running core instead of starting a second one (exit codes 0/1/2); the console loop uses the same host.
- `Commands/AdapterLifecycleControl` (`adapter-lifecycle <stop|unload> <id|name>`): stops native and legacy/Node adapters and optionally removes them from the registry without touching their configuration, as an emergency path when the Admin UI is unavailable. New `CustomAdapterRuntime.UnloadAdapterAsync` and `LegacyAdapterManager.DetachInstanceAsync`; see [Doku/core-commands.md](Doku/core-commands.md).
- Standalone README, architecture documentation and API reference for the separately maintained Admin UI adapter project.

### Changed
- Native adapter files in `Adapters/native` are copied to the build and publish output while preserving their directory structure.
- Adapter directories were renamed: `Adapters/custom` to `Adapters/native` and `Adapters/IOBroker` to `Adapters/legacy`.

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
- Die GitHub-Markdown-Dokumentation unter `Doku/` wurde um `core-bridge.md` und `core-registry.md` erweitert; `architektur.md`, `core-routing.md`, `runtime-lifecycle.md`, `sprintplan.md` und `start.md` wurden aktualisiert.

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
