# Core/Registry – AdapterRegistry

Namespace: `ioBroker_NewGen.Core.Registry`

Zentrale, thread-sichere Registry aller bekannten Adapter (Custom + ioBroker-Legacy).
Wird sowohl von der [TCP-Bridge](core-bridge.md) (Handshake-ID-Vergabe) als auch von der
Node.js-Adapter-Runtime verwendet.

## AdapterStatus

```csharp
public enum AdapterStatus
{
    Created,       // registriert, aber noch keine Verbindung/Prozess
    Connecting,    // Verbindung/Prozess wird aufgebaut
    Connected,     // aktiv und betriebsbereit
    Disconnected,  // geordnet getrennt/gestoppt
    Faulted        // abgestürzt / Fehlerzustand
}
```

## AdapterRegistration

| Feld | Typ | Beschreibung |
| --- | --- | --- |
| Name | string | Sprechender Adaptername (z. B. "mqtt") |
| Id | string | Eindeutige ID, Format "Name.id" (z. B. "mqtt.0") |
| Type | AdapterType | Custom oder IoBroker |
| Status | AdapterStatus | Aktueller Lebenszyklus-Status |
| Metadata | Dictionary<string,string> | Zusätzliche Infos (z. B. scriptPath) |
| TcpEndpoint | string? | Nur für IoBroker: Remote-Endpunkt der TCP-Verbindung |
| InstanceReference | object? | Nur für Custom: Referenz auf geladene Plugin-Instanz (Sprint 3) |
| RegisteredAt | DateTime | Zeitpunkt der Registrierung (UTC) |
| LastSeenAt | DateTime? | Zeitpunkt des letzten Lebenszeichens |

## AdapterRegistry – API

| Methode | Beschreibung |
| --- | --- |
| `Register(name, type, metadata?)` → AdapterRegistration | Registriert neuen Adapter, vergibt automatisch die nächste freie ID ("Name.id") |
| `AssignNextId(name)` → string | Vergibt nur die nächste ID, ohne zu registrieren (intern von `Register` genutzt) |
| `Get(id)` → AdapterRegistration? | Liefert Registrierung zur ID |
| `Exists(id)` → bool | Prüft Existenz |
| `GetAll()` → IReadOnlyCollection<AdapterRegistration> | Alle registrierten Adapter |
| `GetByType(type)` → IReadOnlyCollection<AdapterRegistration> | Gefiltert nach Custom/IoBroker |
| `Unregister(id)` → bool | Entfernt einen Adapter |
| `UpdateStatus(id, status, touchLastSeen = true)` | Aktualisiert Status (+ optional LastSeenAt) |

**ID-Vergabe:** Pro Adaptername wird intern ein Zähler geführt (beginnend bei 0).
Jeder `Register("mqtt", ...)`-Aufruf vergibt die nächste freie Instanznummer,
z. B. "mqtt.0", danach "mqtt.1" usw. – konsistent mit dem ioBroker-Namensschema.

## Verwendung

- **TCP-Bridge** (siehe [core-bridge](core-bridge.md)): Registriert eingehende Legacy-Adapter
  beim Handshake automatisch als `AdapterType.IoBroker`.
- **NodeAdapterRuntime**: Registriert Node.js-Adapter vor dem Prozessstart,
  aktualisiert den Status während Start/Stop/Crash-Recovery.
- **MainRuntimeContext**: Hält die zentrale `AdapterRegistry`-Instanz
  (Property `AdapterRegistry`), initialisiert in `InitializeMatrixAndBus`.
