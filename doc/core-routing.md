# Core/Routing – AdapterRouter

Namespace: `ioBroker_NewGen.Core.Routing`

## AdapterType

```csharp
public enum AdapterType
{
    Custom,    // nativer C#-Adapter, Host-intern, direkt über Systembus
    IoBroker   // Legacy ioBroker-Adapter, isolierter Node.js-Prozess, TCP-Bridge
}
```

## AdapterRouter

Entscheidet anhand des Adaptertyps, ob ein `BusFrame` intern (direkt über den
`SystemMessageBus`) oder extern (über die TCP-Bridge) transportiert wird.

| Methode | Beschreibung |
| --- | --- |
| `Route(BusFrame frame, AdapterType adapterType)` | Routing-Entscheidung: Custom → intern, IoBroker → TCP-Bridge |
| `RouteExternalPayload(BusFrame frame)` | Spiegelt ein von extern empfangenes Payload 1:1 auf den Systembus |
| `AttachTcpBridge(TcpBridgeServer tcpBridge)` | Verknüpft den Router mit der laufenden TCP-Bridge (Sprint 2) |

**Implementierungsstand seit Sprint 2:**

- `AdapterType.Custom` → `frame` wird direkt via `SystemMessageBus.Publish(frame)` veröffentlicht.
- `AdapterType.IoBroker` → sofern eine TCP-Bridge über `AttachTcpBridge` verknüpft wurde,
  wird das Frame über `TcpBridgeServer.SendToAdapterAsync(frame.AdapterId, frame)` an den
  konkreten, per ID adressierten Legacy-Adapter zugestellt. Ist keine Bridge verknüpft
  (z. B. in Tests oder vor der Initialisierung), wird stattdessen eine Warnung geloggt
  (Fallback-Verhalten).

Die Verknüpfung mit der Bridge erfolgt automatisch beim Systemstart über
`MainRuntimeContext.StartTcpBridgeAsync(port)` (siehe [core-bridge](core-bridge.md)).

## Weiterführende Details

Das vollständige TCP-Bridge-Protokoll (Handshake, Framing, Node.js-Adapter-Runtime
mit Crash-Recovery) ist in [core-bridge](core-bridge.md) beschrieben. Die zentrale Verwaltung
aller Adapter (Name, ID, Status, Endpunkt) in [core-registry](core-registry.md).
