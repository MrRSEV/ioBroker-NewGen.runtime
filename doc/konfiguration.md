# Konfiguration (config.toml)

## Speicherort

`config.toml` liegt im Anwendungsverzeichnis (`AppContext.BaseDirectory`,
also neben der ausführbaren Datei). Zuständig: `Startup/ConfigCreator.cs`
(`ConfigCreator.ConfigPath`).

Die persistente Rollen- und Benutzerberechtigung liegt optional als
`roles.json` im selben Anwendungsverzeichnis und wird zusammen mit
`config.toml` und `snapshot.jsonl` in Backups aufgenommen.

## Standard-Inhalt (wird beim Erststart automatisch erzeugt)

```toml
[node]
firstrun = false
nodeId = "newgen-node-01"
instanceName = "NewGenRuntime"
environment = "prod"
basePath = "/var/newgen"
tempPath = "/var/newgen/tmp"
maxParallelAdapters = 8
shutdownTimeoutSeconds = 10
runtimeMode = "standalone"
restartCommand = ""   # optional: Befehl für core-restart, z. B. "systemctl restart ioBroker-NewGen"

[adapter]
adapterDirectory = "/var/newgen/adapters"
customAdapterDirectory = "Adapters/native"
adapterRootPath = "Adapters"
enabledAdapters = ["web"]
requireSignedPackages = false
trustedPackagePublicKeyPem = ""
legacyAutoStart = false
adapterAutoStart = true
adapterIsolationMode = "process"
adapterSandboxing = true
adapterMaxMemoryMB = 256
adapterMaxCpuPercent = 50
adapterStartupTimeoutMs = 3000
adapterCrashRestartDelayMs = 2000
adapterHealthCheckIntervalMs = 5000
adapterApiVersion = "1.0"

[state]
statePersistenceMode = "sqlite"
stateFilePath = "/var/newgen/state.db"
stateAutosaveIntervalMs = 5000
stateMaxHistoryEntries = 10000
stateSchemaVersion = "1.0"
stateCompression = true

[ipc]
ipcMode = "tcp"
ipcHost = "127.0.0.1"
ipcPort = 5001
busQueueSize = 10000
busMaxFrameSize = 65536
busRetryIntervalMs = 200
busMaxSubscribers = 128

[admin]
adminEnabled = true
adminPort = 8080
adminRequireAuthentication = true
adminToken = ""

[web]
enabled = true
listenHost = "127.0.0.1"
port = 8080

[mqtt]
host = "127.0.0.1"
port = 1883
clientId = "newgen-mqtt"
username = ""
password = ""
useTls = false
keepAliveSeconds = 30
topicFilter = "#"
addressPrefix = "mqtt"

[zigbee]
host = "127.0.0.1"
port = 1883
clientId = "newgen-zigbee"
username = ""
password = ""
useTls = false
keepAliveSeconds = 30
topicFilter = "zigbee2mqtt/#"
baseTopic = "zigbee2mqtt"

[hue]
bridgeUri = "http://127.0.0.1"
apiKey = ""
pollIntervalMs = 10000

[shelly]
deviceUri = "http://127.0.0.1"
switchId = 0
pollIntervalMs = 5000

[logging]
logLevel = "info"
logPath = "/var/newgen/logs"
logFileRotationMB = 10
logRetentionDays = 14
enableStructuredLogging = true
enableAdapterLogs = true
adapterLogPath = "/var/newgen/logs/adapters"

[localization]
language = "de_de"
fallbackLanguage = "en_GB"

[security]
allowRemoteControl = false
remoteControlToken = ""
adapterExecutionWhitelist = ["ha", "mqtt", "zigbee"]
adapterExecutionBlacklist = ["debug"]
sandboxFileAccessPolicy = "restricted"
sandboxNetworkPolicy = "local-only"
```

**Hinweis:** `[localization]` steuert die Sprache der AdminUI und der Matrix, siehe [localization](localization.md).

**Hinweis:** `statePersistenceMode = "sqlite"` sowie `stateFilePath` sind
aktuell nur Konfigurationswerte – die tatsächliche State-Persistenz läuft
(Stand Sprint 1) über die JSONL-Snapshot-Datei der `SemanticStateMatrix`
(siehe [core-matrix](core-matrix.md)), nicht über SQLite. Eine spätere Umsetzung ist möglich,
aber noch nicht erfolgt.

Bei aktiviertem `adminRequireAuthentication` zeigt die AdminUI eine
Anmeldemaske (Benutzer/Passwort aus [core-security](core-security.md)). Nach dem Login setzt
der Server automatisch ein Sitzungs-Cookie, ein Token muss nicht mehr eingegeben
werden. `adminToken` ist nur noch ein **optionaler Automations-Token**: bleibt
er leer, ist er deaktiviert und es wird keiner mehr erzeugt. Ist er gesetzt,
akzeptiert die API zusätzlich `X-Admin-Token` oder `Authorization: Bearer <token>`.

## Laden & Registrierung (ConfigLoader)

`Startup/ConfigLoader.cs` liest die Datei zeilenweise:

- Kommentare (`#`) und Leerzeilen werden ignoriert
- Abschnitts-Header `[section]` setzen den aktuellen Namespace
- Zeilen im Format `key = value` werden geparst und unter
  `section.key` (z. B. `node.nodeId`, `ipc.ipcPort`) in das
  RSEV.Utilities-Konfigurationsregister (`rtx.Config`, Typ `IConfig`)
  geschrieben
- Werte werden typisiert erkannt: `bool`, `int`/`long`, JSON-Arrays
  (`["a","b"]`), JSON-Strings (`"text"`), sonst Rohstring

**Erststart-Erkennung:**
- `node.firstrun` wird beim Laden ausgewertet
- Nach dem Laden wird `node.firstrun` **immer** auf `false` gesetzt –
  sowohl im Config-Register als auch persistent in der Datei selbst
- Zusätzlich wird `rtx.Set("HasRunBefore", true)` im GlobalState gesetzt

**Zugriff aus Code:**

```csharp
var nodeId = rtx.Config.Get<string>("node.nodeId");
var port   = rtx.Config.Get<int>("ipc.ipcPort");
```

## Zusammenspiel ConfigCreator → ConfigLoader

- `ConfigCreator.CreateDefaultConfigAsync(rtx)` wird in `FirstRun` aufgerufen
- Erstellt die Datei nur, falls sie noch nicht existiert
- Ruft **in jedem Fall** anschließend `ConfigLoader.LoadIfExistsAsync(rtx)`
  auf, sodass die (neue oder bereits vorhandene) Konfiguration sofort im
  RSEV.Utilities-Register verfügbar ist
- Beim regulären Systemstart (`OnStart`) wird zusätzlich erneut
  `ConfigLoader.LoadIfExistsAsync(rtx)` aufgerufen, um sicherzustellen,
  dass auch bei nachträglichen manuellen Änderungen an der Datei die
  aktuellen Werte geladen werden
