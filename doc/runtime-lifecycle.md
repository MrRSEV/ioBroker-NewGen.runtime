# Runtime & Lifecycle

## Start

`MainRuntimeContext` wird als Singleton aufgebaut. `OnStart` lädt die
Konfiguration und initialisiert in dieser Reihenfolge:

- RSEV.Utilities-MessageBus und EventBus
- SemanticStateMatrix und Snapshot
- TCP-Bridge
- Node.js- und CustomAdapter-Runtime
- Adapter-Deployment-Struktur und BackupService
- Web-/WebSocket-Listener als separater externer `ioBroker-NewGen.web`-Adapter;
  native und Legacy-Adapter registrieren über `IWebEndpointRegistry`.

## Stop

`OnStop` stoppt AdminUI/CustomAdapter, Node-Runtime und TCP-Bridge,
schreibt den Matrix-Snapshot und erzeugt anschließend ein SHA-256-verifiziertes
Backup. Danach werden MessageBus und EventBus kontrolliert heruntergefahren.

## Notfall-Steuerung per Command

Commands (`Commands/`, Basis `RSEV.Utilities.Commands.BaseCommand`) sprechen direkt mit dem Core,
wenn die AdminUI ausfällt oder versehentlich trotz Warnung beendet wurde. Sie benötigen weder UI
noch `RuntimeApiHost`. Details: [core-commands](core-commands.md).

## Unerwartete Beendigung

`UnexpectedUserExitHandler` behandelt CTRL+C, Windows-Exit-Ereignisse und SIGTERM
und führt den gleichen OnStop-Pfad mit Timeout-Schutz aus. Die Commands `core-shutdown` und
`core-restart` (siehe [core-commands](core-commands.md)) nutzen denselben Pfad; ein Neustart erfolgt erst nach
vollständigem OnStop (`node.restartCommand`, systemd-Exitcode 75 oder Selbststart).
