# Changelog

## 2.0.0-beta – Decouple Semantic Embeddings from State History

- Web-/WebSocket-Hosting aus `RuntimeApiHost` herausgelöst: `IWebEndpointRegistry`
  im Core und separates natives Web-Adapterprojekt in
  `Adapters/ioBroker-NewGen.web`; Remote-Repository:
  <https://github.com/MrRSEV/ioBroker-NewGen.web>.
- Direkte `RuntimeApiHost`-/`WebHostService.cs`-Zugriffe werden pro Adapter-
  Instanz protokolliert und isoliert mit Status `Faulted` beendet.
- `AdminUI.dll` registriert bestehende HTTP-Routen über `IWebEndpointRegistry`,
  verwendet keinen eigenen Listener mehr und hängt vom `web`-Adapter ab.
- Die Standardkonfiguration aktiviert den separaten `web`-Adapter und ergänzt
  `web.enabled`, `web.listenHost` und `web.port`.

- `NodeAdapterRuntime.Api` stellt die Kompatibilitätsdienste für fortgeschrittene
  Legacy-Adapter bereit: `sendTo`, State-/Objekt-Subscriptions, Notifications,
  Metadata, adapter-isolierter File Storage, Web-Endpoint-Registrierung über
  `IWebEndpointRegistry` und Process Lifecycle. `adapterCreatesOwnWebServer` ist
  `false`; die TCP-Bridge verarbeitet die Query-/Command-Frames und liefert
  Antworten bzw. Push-Events.

- `MatrixObject` verwaltet `Embedding`, `LastEmbeddingUpdate` und
  `SemanticDirty`.
- `StateNode` und `HistorySample` sind frei von Embeddings; History-Samples
  enthalten nur `Id`, `Timestamp` und `Value`.
- `SetState()` und StateUpdate-Frames bleiben im Realtime-Pfad ohne
  Embedding-Generierung und VectorIndex-Update.
- `SemanticEmbeddingWorker` verarbeitet dirty Objekte im Hintergrund alle
  30 Sekunden, aktualisiert den Index inkrementell und unterstützt explizite
  Reindex-Anfragen.
- Vollständiger `RebuildVectorIndex()` und Snapshot-Migration für alte
  History-Zeilen ergänzt.
- `AdminUI.dll` aus dem Core-Solution-/Publish-Build entfernt. Die optionale
  DLL wird nur berücksichtigt, wenn sie manuell unter `Adapters/native` liegt;
  der ReleaseCandidateBuilder übernimmt eine separat gebaute DLL automatisch
  in Release-Artefakte, sofern sie im Workspace vorhanden ist.
- Lifecycle-Integration in `OnStart`/`OnStop` sowie neue Dokumentation in
  [semantic-embeddings](semantic-embeddings.md) und [core-matrix](core-matrix.md).

## dev-0.4.0 – AdminUI: Adapter-Verwaltung

- AdminUI: Adapter-Abschnitt mit Tabs „Display as Table“ (Standard) und „Display as JSON“;
  aufklappbare Baum-Tabellen auch für States, Konfiguration, Rollen und Benutzer/Rollen.
- Adapter-Metriken (Laufzeit, Start/Stopp-Zeitpunkt, Starts, Neustarts, Fehler, letzter Fehler)
  und Lifecycle-Status; neue Registry-Felder und `RecordError`/`RecordRestart`.
- Lifecycle-Aktionen Start, Stopp, Neustart (`/api/adapters/restart`, `RestartAdapterAsync`);
  sichtbar und serverseitig nur mit `AdapterStart`/`AdapterStop`/`AdapterRestart`.
  `/api/auth/me` liefert zusätzlich `permissions`.
- Sicherheitsabfrage (`confirm`) vor Stopp/Neustart des Admin-Adapters.
- Fix: Admin-Adapter startete nach Neustart den RuntimeApiHost nicht mehr (Registrierungs-Flag
  wurde nicht zurückgesetzt, Stopp wartet nun auf den Host).
- Autostart-Checkbox je Custom-Adapter (außer `admin`), speichert in `adapter.enabledAdapters`
  (`/api/adapters/autostart`, Recht `SystemConfigure`).
- Fix: Sprach-Dropdown zeigte „undefined“ (camelCase-Felder `code`/`name`).
- Neue Übersetzungsschlüssel (`action.*`, `metric.*`, `table.*`, `confirm.*`) in de_DE und en_GB;
  andere Sprachen fallen auf en_GB zurück.

## beta0.3.0 – Core.Security (in Arbeit)

- Neu: eingebetteter **LocalizationService** (siehe [localization](localization.md)): Sprachdateien
  für 24 EU-Sprachen, Config `[localization] language = "de_de"`, API `/api/i18n/*`,
  Sprach-Dropdown in der AdminUI, Matrix-State `system.localization.language`
  und optionale Adapter-Übersetzungen (`IAdapter.Translations` / Ordner `Localization`).

- Neues Modul `ioBroker.Core.Security` (siehe [core-security](core-security.md)): SQLite-
  Datenbank `data/security.db`, Benutzer, Rollen mit Vererbung (Viewer,
  Operator, Developer, Administrator), Rechte, Bearer-Token-Sitzungen und
  Audit-Log.
- Passwort-Hashing mit BCrypt, Sitzungstoken nur als SHA-256-Hash gespeichert.
- Sieben Security-Events am `SystemEventBus`; Matrix-Spiegel für
  `system.security.*` ohne Geheimnisse.
- Bootstrap-Administrator `admin` mit erzwungenem Passwortwechsel.
- Neue Abhängigkeiten: BCrypt.Net-Next 4.2.0, Microsoft.Data.Sqlite 9.0.
- Datenbankzugriff über RSEV.Utilities (`SqliteAdapter`, `ConnectionFactory`).
- AdminUI: Anmeldemaske mit automatischem Sitzungs-Cookie (`ngsession`),
  Rechteprüfung je Endpunkt, erzwungener Passwortwechsel; kein Admin-Token
  mehr nötig (`adminToken` nur noch optional für Automatisierung).
- Offen: API-Keys, Passwortrichtlinie, Account-Lockout, Admin-Menü (Benutzer/Sitzungen).

## beta0.2.0 – Sprint-1-bis-11-Refactoring

- RSEV.Utilities 3.0.0.0 als verbindliche Bus-, Event-, Runtime- und
  Prozessinfrastruktur integriert.
- AdminUI sowie alle nativen Phase-1/2/3-Adapter als separate DLL-Projekte
  mit optionalen Adapter-Dependencies.
- Node.js-Bridge-Client, echte Node-Prozess-Bridge-Smokes, Paketmanifest,
  Repository-Download, Authentifizierung und Release-Candidate-Hashprüfung.
- Optionale RSA-SHA256-Signatur und Trust-Key-Verifikation für Release-
  Candidate-Manifeste.
- ReaderWriterLockSlim-Konsistenz in der StateMatrix, geschützte StateNode-
  Getter, Definition-Validierung und Dependency-sichere Shutdown-Reihenfolge.
- Lokale Protokoll-Fixtures für alle zehn nativen Adapter sowie verpflichtende
  Remote-RSA-Signaturen gegen einen konfigurierten Trust-Key.
- Admin-Dashboard mit Token-Requests, konfigurierbare Adapter-Defaults und
  kompatible SendPayload-API.
- Persistente Benutzer-/Rollenverwaltung mit Deny-wins-State-Rechten und
  Admin-Endpunkten für Rollen und Benutzerzuweisungen.
- Konfigurationsbasierte MQTT-/Zigbee-/Hue-/Shelly-Instanzen, MQTT-Keepalive
  und gemeinsamer retry-fähiger HTTP-Polling-Lifecycle.
- Integrationstests für Matrix, Router, Bridge, Node.js, MQTT, Modbus,
  Dependencies, Admin-Auth, Backup, Pakete und Release-Artefakte.

Diese Seite ergänzt den historischen Projekt-`CHANGELOG.md` um den aktuellen
beta0.2.0-Stand und die Sprint-Zuordnung.

## [0.1.0] - 2026-10-05

**Tag:** `dev-0.1.0`

### Hinzugefügt

- Grundgerüst des Runtime-Lifecycles (`FirstRun`, `OnStart`, `OnStop`) auf Basis von RSEV.Utilities.
- `MainRuntimeContext` als applikationsweiter Singleton-RuntimeContext inkl. eigenem Logger
  (`SystemLog`, schreibt `latest.log` in das Basisverzeichnis) und Konfigurationsregister (`ConfConfig`).
- `ConfigCreator` zum automatischen Anlegen einer Standard-`config.toml` beim Erststart.
- `ConfigLoader` zum Einlesen der `config.toml` in das RSEV.Utilities-Konfigurationsregister
  beim Systemstart, inklusive automatischem Zurücksetzen von `firstrun` auf `false`.
- `UnexpectedUserExitHandler` zur sauberen Behandlung von CTRL+C, Konsolen-Schließen sowie
  Windows-Shutdown-/Logoff-Events mit garantiertem `OnStop`-Aufruf.
- **Core/Matrix** – Semantische Zustands- und Referenzmatrix (`SemanticStateMatrix`):
  - `MatrixObject`, `StateNode`, `HistorySample` als Kernmodelle des Objektbaums.
  - `IEmbeddingService` / `HashingEmbeddingService` für deterministische semantische Embeddings.
  - `IVectorIndex` / `VectorIndex` für semantische ähnlichkeitssuche (Kosinus-Similarity).
  - `IRoleService` / `DefaultRoleService` für Lese-/Schreibrechte pro Benutzer und State.
  - Vollständige JSONL-Snapshot-Persistenz (`LoadSnapshotAsync`, `WriteSnapshotAsync`,
    periodischer `StartCronSnapshot`).
  - API: `RegisterObject`, `GetObject`, `GetState`, `SetState`, `Subscribe`/`Unsubscribe`,
    `SemanticQuery`, `GetHistory`, `DisplayAsJson`.
- **Core/Bus** – Zentraler neutraler Systembus:
  - `BusFrame`-Record (AdapterId, Address, Payload, ValueType, Kind, Timestamp, Metadata).
  - `SystemMessageBus` (basierend auf `RSEV.Utilities.Messaging.BaseMessageBus`) mit
    thread-sicherem Publish/Subscribe inkl. Wildcard-Abonnements.
- **Core/Routing** – `AdapterRouter`-Skeleton zur Unterscheidung zwischen internem Routing
  (`CustomAdapter`) und TCP-Bridge-Routing (`IoBrokerAdapter`). Der TCP-Bridge-Teil wurde
  im nachfolgenden Sprint 2 ausgebaut.
- Registrierung von `SemanticStateMatrix`, `SystemMessageBus` und `AdapterRouter` im
  `MainRuntimeContext`, Initialisierung beim Systemstart sowie automatisches Schreiben des
  finalen Snapshots beim Herunterfahren.

[Release-Tag auf GitHub](https://github.com/MrRSEV/ioBroker-NewGen/releases/tag/dev-0.1.0)
