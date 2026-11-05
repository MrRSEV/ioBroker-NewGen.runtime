# Core/Bridge – TCP-Bridge & Node.js-Adapter-Runtime

Namespace: `ioBroker_NewGen.Core.Bridge`

Seit **Sprint 2** verfügt das System über eine funktionierende TCP-Bridge zur
Anbindung isolierter Legacy-ioBroker-Adapter (Node.js-Prozesse) sowie eine
Node.js-Prozess-Runtime mit Crash-Recovery und Heartbeat-Überwachung.

## Protokoll

### BridgeMessageType

```csharp
public enum BridgeMessageType
{
    Handshake,     // Adapter → Host: Erstkontakt, sendet eigenen Namen
    HandshakeAck,  // Host → Adapter: bestätigt Handshake, teilt vergebene ID mit
    Frame,         // bidirektional: transportiert ein BusFrame
    Heartbeat,     // bidirektional: Lebenszeichen
    Error          // bidirektional: Fehlermeldung
}
```

### BridgeEnvelope

Kompaktes JSON-Transport-Envelope (kein reines Binärprotokoll, aber kompakt):

| Feld | Beschreibung |
| --- | --- |
| Type | `BridgeMessageType` |
| AdapterId | Beim Handshake: Adaptername; danach: vergebene ID ("Name.id") |
| Address | Logische Zieladresse (State-ID) eines Frames |
| ValueType | Typbezeichnung des Payloads |
| FrameKind | `BusFrameKind` als int (entkoppelt Draht-Format vom Bus-Modell) |
| PayloadBase64 | Payload, Base64-kodiert |
| Metadata | Key/Value-Metadaten |
| ErrorMessage | Nur bei `Error` gesetzt |
| Timestamp | UTC-Zeitstempel |

### BridgeProtocol – Framing

Jede Nachricht wird mit einem **4-Byte-Big-Endian-Längenpräfix** gesendet,
gefolgt von der UTF-8-kodierten JSON-Nutzlast. Dadurch sind Nachrichtengrenzen
im TCP-Stream eindeutig erkennbar, unabhängig von TCP-Fragmentierung.

- `BridgeProtocol.WriteAsync(stream, envelope)` – serialisiert + schreibt mit Längenpräfix
- `BridgeProtocol.ReadAsync(stream)` – liest Längenpräfix, dann exakt so viele Bytes, deserialisiert

## BridgeConnection

Kapselt eine einzelne TCP-Verbindung (`TcpClient` + `NetworkStream`):

- `AdapterId` – gesetzt nach erfolgreichem Handshake
- `LastSeenAt` – Zeitpunkt des letzten empfangenen Envelopes
- `ReadAsync` / `SendAsync` (Schreibzugriffe werden über ein `SemaphoreSlim`
  serialisiert, um parallele Schreibvorgänge auf demselben Stream zu verhindern)

## TcpBridgeServer

Zentraler TCP-Server, lauscht auf einem festen Host-Port (Default: `ipc.ipcPort`
aus `config.toml`, Fallback 5001).

**Handshake-Ablauf:**
- Adapter verbindet sich per TCP
- Adapter sendet `BridgeMessageType.Handshake` mit seinem Namen (`AdapterId`-Feld enthält den Namen, z. B. "mqtt")
- Host registriert den Adapter in der `AdapterRegistry` (Typ `AdapterType.IoBroker`) und vergibt
  eine eindeutige ID im Format **"Name.id"** (z. B. "mqtt.0", "mqtt.1", ...)
- Host sendet `BridgeMessageType.HandshakeAck` mit der vergebenen ID zurück
- Ab sofort werden `Frame`- und `Heartbeat`-Nachrichten bidirektional ausgetauscht

**Eingehende Frames** (Adapter → Host): Werden 1:1 auf den `SystemMessageBus`
gespiegelt (`HandleIncomingFrame` → `SystemMessageBus.Publish`).

**Ausgehende Frames** (Host → Adapter): `SendToAdapterAsync(adapterId, frame)`
sucht die passende `BridgeConnection` über die Adapter-ID und sendet das Frame
als `BridgeMessageType.Frame`-Envelope.

Bei Verbindungsabbruch wird der Adapterstatus in der `AdapterRegistry` auf
`Disconnected` gesetzt und die Verbindung aus der internen Map entfernt.

## NodeAdapterRuntime

Verwaltet isolierte Node.js-Prozesse für Legacy-ioBroker-Adapter, basierend auf
den dekompilierten RSEV.Utilities-Prozessklassen:

- `RSEV.Utilities.Processes.NodeProcess` – startet `node <script> <args>` als Subprozess,
  leitet stdout/stderr um, klassifiziert Log-Zeilen (Error/Warning/Info)
- `RSEV.Utilities.Processes.RuntimeProcessController` – zentrale Prozessverwaltung
  (Register, Start/Stop/Restart, Health-Checks, Auflistung) – bereits über
  `IRuntimeContext.ProcessController` im `MainRuntimeContext` verfügbar

**API von NodeAdapterRuntime:**

| Methode | Beschreibung |
| --- | --- |
| `StartAdapterAsync(name, scriptPath, arguments?)` | Registriert Adapter in `AdapterRegistry` (Typ IoBroker), startet `NodeProcess` über `RuntimeProcessController` |
| `StopAdapterAsync(adapterId)` | Stoppt den zugehörigen Node-Prozess, Status → Disconnected |
| `StartHealthMonitoring()` / `StopHealthMonitoring()` | Periodischer Timer (Default 5000 ms, aus `adapterHealthCheckIntervalMs`) |
| `StopAllAsync()` | Stoppt alle verwalteten Prozesse (z. B. bei Systemstopp) |

### NodeAdapterRuntime API für Legacy-Kompatibilität

`NodeAdapterRuntime.Api` stellt die Kompatibilitätsdienste für fortgeschrittene
Legacy-Adapter bereit. Das Ziel ist:

> Provide compatibility services for advanced legacy adapters.

Die API umfasst:

| Dienst | Vertrag |
| --- | --- |
| `SendToAsync(...)` | `sendTo`-/`sendToHost`-Nachrichten als Bridge-Frames mit `command`, `from` und JSON-Payload |
| `SubscribeStates` / `UnsubscribeStates` | State-Subscriptions mit `*`-Mustern; State-Änderungen werden als `StateChange` gepusht |
| `SubscribeObjects` / `UnsubscribeObjects` | Objekt-Subscriptions mit `*`-Mustern; Änderungen werden als `ObjectChange` gepusht |
| `Notify(...)` | Host-Notifications inklusive Level, Text und optionalem Payload |
| `GetMetadata` / `SetMetadata` | Zugriff auf die Metadaten der `AdapterRegistry`-Registrierung |
| `ReadFileAsync`, `WriteFileAsync`, `DeleteFile`, `ListFiles` | Adapter-isolierter Dateispeicher unter `data/adapter-storage/<adapterId>`; Pfadüberquerung wird abgewiesen |
| `RegisterWebEndpoint(...)` | Registrierung bei der zentralen `IWebEndpointRegistry`; der Listener läuft im externen Web-Adapter |
| `StartAdapterAsync`, `StopAdapterAsync`, `RestartAdapterAsync` | Prozess-Lifecycle über `NodeAdapterRuntime` |

Die Vertragsbeschreibung ist zusätzlich über `NodeAdapterRuntime.Api.Contract`
abrufbar. Sie enthält die acht Features und die Web-Integration:

```text
delegateTo: WebEndpointRegistry
adapterCreatesOwnWebServer: false
```

Ein Legacy-Adapter erstellt daher keinen eigenen Webserver. Die Bridge
beantwortet außerdem `StateQuery`, `ObjectQuery`, `ObjectCreate`,
`ObjectExtend`, `ObjectDelete`, `Subscribe`, `Unsubscribe`, `Message`,
`HostMessage`, `Metadata`, `FileStorage`, `WebEndpoint` sowie WebSocket-Frames.
Antworten tragen
`metadata.reply=true` sowie die ursprüngliche `correlationId`.

Die gebündelte `Legacy/AdapterCore`-API stellt dafür unter anderem
`notifyAsync`, `getMetadataAsync`, `setMetadataAsync`, `readFileAsync`,
`writeFileAsync`, `deleteFileAsync`, `listFilesAsync`,
`registerWebEndpointAsync(route, handler, options)`, `getProcessStatusAsync`,
`restartSelfAsync` und `stopSelfAsync` bereit. Mit
`options.kind = "websocket"` werden Open-, Message- und Close-Ereignisse über
die Bridge weitergeleitet.

Für die Socket.IO-Kompatibilität des originalen Admin-Adapters gibt es außerdem
`publishInstanceMessageAsync(messageType, socketId, data, { adminInstance })`.
Es sendet `sendTo(adminInstance, "im", { m, s, d })`; standardmäßig ist das Ziel
`admin.0`. Der Admin-Adapter veröffentlicht diese Nachricht mit
`publishInstanceMessageAll` an seine Socket.IO-Clients. Der normale Aufruf
`sendTo("admin.0", "im", { m, s, d })` wird über dieselbe Message-Bridge
zugestellt.

Der originale `ioBroker.web`-Adapter besitzt keinen generischen
`registerWebEndpoint`-Vertrag. Er lädt `common.webExtension`-Module mit Express-
und Socket.IO-Zugriff. Solche installierten Legacy-Extensions verbleiben auf
diesem Host; NewGen-Adapter registrieren HTTP-/WebSocket-Routen über
`IWebEndpointRegistry` und den NewGen-Web-Adapter. Ein automatisches Umschalten
beliebiger dynamischer Routen auf den klassischen `web`-Adapter ist mit dessen
öffentlicher API nicht möglich.

Auch `IAdapterContext.RuntimeContext.ApiHost` ist im adapterbezogenen Context
gesperrt; der Context-Proxy verwendet denselben isolierten Fehlerpfad.

Native Adapter mit bestehenden `RSEV.Utilities.ApiBuilder.IRuntimeEndpoint`-
Klassen können die Kompatibilitäts-Erweiterung
`IWebEndpointRegistry.Register(ownerId, endpoint)` verwenden. Sie übernimmt
Request und Response in den neuen Registry-Vertrag, ohne einen Listener im
Adapter zu starten.

**Crash-Recovery:** Der Health-Timer ruft für jeden verwalteten Prozess
`CheckHealthAsync()` auf. Liefert dieser `false` (Prozess beendet/abgestürzt),
wird der Adapterstatus auf `Faulted` gesetzt, nach `adapterCrashRestartDelayMs`
(Default 2000 ms) ein `RestartAsync()` ausgeführt und der Status entsprechend
auf `Connected` oder erneut `Faulted` aktualisiert.

## Integration in MainRuntimeContext

| Property / Methode | Beschreibung |
| --- | --- |
| `AdapterRegistry` | Zentrale Registry aller Adapter (Custom + IoBroker) |
| `TcpBridge` | Die laufende `TcpBridgeServer`-Instanz (oder `null` vor Start) |
| `NodeRuntime` | Die `NodeAdapterRuntime`-Instanz (oder `null` vor Initialisierung) |
| `StartTcpBridgeAsync(port)` | Erstellt + startet die TCP-Bridge, verknüpft sie mit dem `AdapterRouter` |
| `InitializeNodeRuntime()` | Erstellt die `NodeAdapterRuntime` (nutzt `ProcessController` aus RSEV.Utilities) und startet die Health-Überwachung |
| `ShutdownBridgeAndNodeRuntimeAsync()` | Stoppt Node-Runtime und TCP-Bridge geordnet (aufgerufen in `OnStop`) |

## Ablauf beim Systemstart (OnStart)

- `InitializeMatrixAndBus(snapshotPath)` – wie Sprint 1
- Port aus `rtx.Config.Get<int>("ipc.ipcPort")` lesen (Fallback 5001)
- `StartTcpBridgeAsync(port)` – TCP-Bridge starten, mit `AdapterRouter` verknüpfen
- `InitializeNodeRuntime()` – Node-Runtime erzeugen, Health-Monitoring starten

## Ablauf beim Systemstopp (OnStop)

- `ShutdownBridgeAndNodeRuntimeAsync()` – stoppt zuerst alle Node-Prozesse,
  danach die TCP-Bridge (inkl. aller offenen Verbindungen)
- anschließend wie gewohnt: Snapshot der `SemanticStateMatrix` schreiben
