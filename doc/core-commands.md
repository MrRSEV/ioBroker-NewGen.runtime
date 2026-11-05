# Core/Commands – Direkte Steuerung des Cores

Namespace: `ioBroker_NewGen.Commands`

Commands sind kleine, UI-unabhängige Einstiegspunkte in den Core. Sie sind gedacht für den Fall,
dass die AdminUI ausgefallen ist oder ein Nutzer den UI-Adapter trotz Warnung beendet hat – sie
arbeiten direkt gegen den `MainRuntimeContext` (Registry, Custom- und Node-Runtime,
Legacy-Manager). Basis sind `ICommand`/`BaseCommand`/`CommandHandler` aus `RSEV.Utilities.Commands`.

## Klassenübersicht

| Klasse | Datei | Beschreibung |
| --- | --- | --- |
| `AdapterLifecycleControl` | Commands/AdapterLifecycleControl.cs | Stoppt native und Legacy-/Node-Adapter und entfernt sie optional aus der Registry |
| `CoreLifecycleControl` | Commands/CoreLifecycleControl.cs | `core-shutdown` / `core-restart`: geordnetes Beenden/Neustarten des Cores über OnStopAsync |
| `CoreCommandHost` | Commands/CoreCommandHost.cs | Registriert Commands, parst Konsolenzeilen, einheitlicher Einstieg für Konsole und Pipe |
| `CommandPipeServer` / `CommandPipeClient` | Commands/CommandPipe*.cs | Lokaler Steuerkanal für CLI-Aufrufe gegen den laufenden Core |

## adapter-lifecycle

```text
adapter-lifecycle <stop|unload> <id|name>
```

| Aktion | Wirkung |
| --- | --- |
| `stop` | Adapter stoppen; Registrierung bleibt bestehen (Status `Disconnected`). |
| `unload` | Adapter stoppen und aus Runtime und `AdapterRegistry` entfernen. Konfiguration und persistierte Instanzliste (`adapter.instances`, `adapter.legacyInstances`) bleiben erhalten; auch die Instanz `.0` kann entladen werden. |

Das Ziel ist eine Instanz-ID (`alexa.1`) oder ein Adaptername (`alexa`, betrifft alle Instanzen).
Die Auflösung nach Typ erfolgt automatisch:

- `Custom` (nativ): `CustomAdapterRuntime.StopAdapterAsync` bzw. `UnloadAdapterAsync`.
- Legacy-Instanz: `LegacyAdapterManager.StopInstanceAsync` bzw. `DetachInstanceAsync`.
- Sonstige Node-Adapter: `NodeAdapterRuntime.StopAdapterAsync`; bei `unload` zusätzlich `AdapterRegistry.Unregister`.

Unterschied zu `RemoveInstanceAsync`: `unload` löscht weder Konfiguration noch Instanzeintrag und
eignet sich daher, um Adapter-DLLs oder Node-Adapter im laufenden Betrieb auszutauschen
(stoppen/entladen → Dateien ersetzen → Adapter wieder starten).

### Grenzen

- Der `AssemblyLoader` lädt in den Default-Kontext; eine bereits geladene native Adapter-DLL
  wird dadurch nicht aus dem Prozess entladen. Für Node-/Legacy-Adapter gilt das nicht.
- Rückmeldung an CLI/Pipe ist „ausgeführt“ bzw. Fehler; Details schreibt der Command ins Log.

## Aufrufwege

Jeder beim `CoreCommandHost` (`MainRuntimeContext.Commands`) registrierte Command ist automatisch auf
allen Wegen verfügbar:

| Weg | Beispiel |
| --- | --- |
| Konsole des laufenden Cores | `adapter-lifecycle stop alexa.0` (Anführungszeichen für Leerzeichen, `help` listet alle Commands) |
| CLI gegen laufenden Core | `ioBroker-NewGen.exe -stop alexa.0`, `-unload alexa`, `-cmd <name> <args…>` oder `-<name> <args…>` |

Die CLI startet **keinen** zweiten Core: `Main` erkennt Argumente mit führendem `-`, schickt sie über den
lokalen Steuerkanal an den laufenden Core und beendet sich. Exitcodes: `0` ok, `1` Command fehlgeschlagen/unbekannt,
`2` kein Core erreichbar. `-stop`/`-unload` sind Kurzformen für `adapter-lifecycle stop|unload`.

### Steuerkanal

`CommandPipeServer` (Start in `OnStart` direkt nach Matrix/Bus, damit er auch bei hängendem Start
verfügbar ist) nutzt `NamedPipeServerStream`: unter Windows eine Named Pipe, unter Linux/macOS ein
Unix-Domain-Socket – plattformunabhängig und nur für denselben Benutzer (`CurrentUserOnly`). Der Pipe-Name
`ioBroker-NewGen-<hash>` leitet sich vom Installationsverzeichnis ab. Protokoll: eine JSON-Zeile
(`["adapter-lifecycle","stop","alexa.0"]`) → Antwortzeile `OK …` oder `ERR …`.

`CommandPipeClient` enthält die gesamte Client-Logik (Argumente parsen, senden) und hängt nicht an der exe;
ein anderer Host (Linux-ELF, `dotnet ioBroker-NewGen.dll`, eigenes Tool) ruft nur
`CommandPipeClient.TryParseArguments` und `RunAsync` auf.

## core-shutdown / core-restart

```text
core-shutdown
core-restart
```

Beide führen den Shutdown **immer über `OnStop.OnStopAsync()`** aus (nie per Prozess-Kill): AdminUI/CustomAdapter,
Node-/Legacy-Adapterprozesse und TCP-Bridge werden gestoppt, Snapshot und Backup geschrieben, die Busse
heruntergefahren. Erst danach endet der Prozess regulär. Der Command kehrt sofort zurück (der Shutdown läuft
im Hintergrund), sodass CLI und Pipe noch ihre Antwort erhalten. Läuft bereits ein Shutdown (z. B. CTRL+C),
wird der zweite Aufruf ignoriert.

CLI-Kurzformen: `ioBroker-NewGen.exe -shutdown` und `ioBroker-NewGen.exe -restart`. In der Konsole des Cores
beendet weiterhin `exit`. `-restart` meint den Core; Adapter-Neustarts laufen über AdminUI/API.

### Neustart-Ablauf

Nach erfolgreichem OnStop wählt `CoreRestarter` (in dieser Reihenfolge):

- `node.restartCommand` in der `config.toml` gesetzt: Der Befehl wird per `cmd /c` (Windows) bzw. `/bin/sh -c`
  gestartet, z. B. `systemctl restart ioBroker-NewGen`, `C:\\skripte\\restart.bat` oder `/opt/newgen/restart.sh`.
- Prozess läuft unter systemd (Umgebungsvariable `INVOCATION_ID`): Exitcode `75`. Die Unit startet dann neu mit
  `Restart=on-failure` (oder `Restart=always` bzw. `RestartForceExitStatus=75`). Ein Kindprozess würde beim
  Beenden der Unit mitgestoppt, deshalb übernimmt hier der Supervisor.
- Sonst: Der Core startet sich selbst als neuen Prozess (gleiche Argumente) und beendet sich.

Ist OnStop nicht sauber (Timeout/Fehler) durchgelaufen, wird **kein** Neustart ausgeführt (Exitcode `1`), damit
nicht zwei Cores gleichzeitig um Ports und Datenbank konkurrieren.

### Beenden per Signal

Neben CTRL+C und Windows-Exit-Ereignissen löst auch `SIGTERM` (z. B. `systemctl stop`) den Shutdown über
OnStopAsync aus. Ohne Konsole (stdin geschlossen, Dienstbetrieb) wird die Konsolenschleife beendet; Steuerung
erfolgt dann über Signale und die Command-Pipe.

### Eigene Commands

- Von `BaseCommand` erben, eindeutigen `Name` und `Description` setzen.
- `ExecuteAsync(IRuntimeContext, params string[])` implementieren; der Kontext ist der `MainRuntimeContext`.
- Beim `CoreCommandHost` registrieren (`MainRuntimeContext.Commands.Register(...)`; Namen sind case-insensitiv).
  Danach ist er in Konsole und CLI (`-<name> …`) ohne weiteren Code verfügbar.
