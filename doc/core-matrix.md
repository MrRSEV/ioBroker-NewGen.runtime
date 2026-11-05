# Core/Matrix – Semantische Zustandsmatrix

Namespace: `ioBroker_NewGen.Core.Matrix`

Die `SemanticStateMatrix` trennt seit `2.0.0-beta` den Echtzeit-Wertpfad von
der semantischen Verarbeitung. `StateNode` und `HistorySample` enthalten nur
Wertdaten; semantische Embeddings gehören zum `MatrixObject`.

## Klassenübersicht

| Klasse / Interface | Beschreibung |
| --- | --- |
| `SemanticStateMatrix` | Objekte, States, Subscriptions, History, Snapshot und Indexzugriff |
| `MatrixObject` | Objektidentität, Metadaten und genau ein semantisches Embedding |
| `StateNode` | Aktueller Wert, Zeitstempel, Schreiber und In-Memory-History |
| `HistorySample` | Record aus `Id`, `Timestamp` und `Value` |
| `SemanticEmbeddingWorker` | Hintergrundverarbeitung dirty markierter Objekte, Standardintervall 30 s |
| `IEmbeddingService` | Austauschbarer Dienst für Text-Embeddings |
| `IVectorIndex` / `VectorIndex` | Inkrementeller Brute-Force-Kosinusindex |
| `SnapshotEntry` | JSONL-Format für Objekt- und History-Zeilen |

## Datenmodell

```csharp
public sealed class MatrixObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string> Meta { get; init; } = new();
    public float[] Embedding { get; }
    public DateTime LastEmbeddingUpdate { get; }
    public bool SemanticDirty { get; }
}

public sealed record HistorySample(
    string Id,
    DateTime Timestamp,
    object? Value);
```

`Embedding`, `LastEmbeddingUpdate` und `SemanticDirty` werden intern durch den
Worker verwaltet. `DateTime.MinValue` bedeutet, dass noch kein erfolgreiches
Embedding gespeichert wurde.

`StateNode` hält `Value`, `Timestamp`, `LastWrittenBy`, `Meta` und `History`.
Es besitzt kein `Embedding` mehr. Ein reguläres Wertupdate verändert weder den
semantischen Index noch den Dirty-Status.

## Ablauf

### Echtzeitpfad

```text
SetState()/StateUpdate-Frame
  -> Wert validieren
  -> HistorySample(Id, Timestamp, Value) erzeugen
  -> StateNode speichern
  -> Subscriber und StateChanged benachrichtigen
```

Die Abläufe enthalten keine Embedding-Generierung und keinen Vektorindex-
Zugriff. Bei einem unbekannten State wird nur ein neues MatrixObject angelegt
und für die spätere semantische Verarbeitung markiert.

### Semantikpfad

```text
RegisterObject / UpdateObject / RequestSemanticReindex
  -> MatrixObject.SemanticDirty = true
  -> SemanticReindexQueue
  -> SemanticEmbeddingWorker (30 s)
  -> Source-Text aus Id, Name, Type und Meta bauen
  -> Embedding erzeugen
  -> MatrixObject speichern
  -> VectorIndex inkrementell aktualisieren
  -> SemanticDirty = false
```

Der Worker erzeugt das Embedding außerhalb des Matrix-Locks. Ändert sich ein
Objekt währenddessen erneut, verwirft die Matrix das veraltete Ergebnis und
verarbeitet das Objekt im nächsten Lauf erneut.

## API für semantische Verarbeitung

| Methode | Beschreibung |
| --- | --- |
| `RegisterObject(MatrixObject)` | Registrierung; markiert das Objekt dirty |
| `UpdateObject(MatrixObject)` | Aktualisiert Name, Typ, Meta oder StateDefinition; semantische Änderungen markieren dirty |
| `RequestSemanticReindex(string)` / `ReindexObject(string)` | Explizite Reindex-Anforderung |
| `ProcessDirtySemanticObjects(CancellationToken)` | Ein Worker-Lauf, synchron für Flush und Tests |
| `RebuildVectorIndex()` | Vollständiger Index-Rebuild aus gespeicherten MatrixObject-Embeddings |
| `SemanticQuery(string, int)` | Suche über Objekt-Embeddings |

## Snapshot und Migration

Das JSONL-Format enthält `Kind: "object"`-Zeilen für Objektmetadaten,
Embedding, `LastEmbeddingUpdate` und `SemanticDirty`. `Kind: "history"`-Zeilen
enthalten nur `Id`, `Timestamp` und `Value`; `Embedding` wird dort nicht mehr
geschrieben.

Alte Snapshots mit Embeddings in History-Zeilen bleiben lesbar. Diese alten
Embeddings werden nicht in den neuen State übernommen; die Objekte werden
dirty markiert und durch den Worker aus ihren aktuellen semantischen Daten
neu erzeugt. Ein `RebuildVectorIndex()` kann den Index jederzeit aus dem
gespeicherten MatrixObject-Bestand neu aufbauen.

## Lifecycle

`MainRuntimeContext.InitializeMatrixAndBus` erzeugt den Worker. `OnStart`
startet ihn nach dem Snapshot-Laden, `OnStop` stoppt ihn und verarbeitet
ausstehende dirty Objekte vor dem finalen Snapshot.

Der Default-Embeddingdienst bleibt `HashingEmbeddingService` (128 Dimensionen)
und kann später durch ONNX oder einen anderen `IEmbeddingService` ersetzt
werden. Der aktuelle Index ist bewusst austauschbar; HNSW ist eine mögliche
spätere Erweiterung.
