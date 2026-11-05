# Decouple Semantic Embeddings from State History

Version: `2.0.0-beta`  
Status: umgesetzt

## Ziel

Semantische Verarbeitung ist aus dem Realtime-Schreibpfad herausgelöst.
Häufige Temperatur-, Feuchte-, Leistungs- oder sonstige Wertupdates erzeugen
keine Embeddings mehr und aktualisieren nicht den Vektorindex. Dadurch bleiben
CPU-Aufwand, Speicherbedarf und Latenz von `SetState()` unabhängig von der
Embedding-Komplexität.

## Änderungen

- `MatrixObject` besitzt `Embedding`, `LastEmbeddingUpdate` und
  `SemanticDirty`.
- `StateNode` besitzt kein Embedding mehr.
- `HistorySample` enthält nur `Id`, `Timestamp` und `Value`.
- `SetState()` und `ApplyFrame()` schreiben nur State/History und benachrichtigen
  Subscriber.
- Registrierung, Änderungen an Name/Typ/Meta/StateDefinition und explizite
  Reindex-Anforderungen markieren ein Objekt dirty.
- `SemanticEmbeddingWorker` verarbeitet dirty Objekte alle 30 Sekunden im
  Hintergrund. Die Verarbeitung nutzt eine `ConcurrentQueue<string>` und eine
  zusätzliche Dirty-Prüfung, sodass auch Snapshot-/Restart-Szenarien sicher
  verarbeitet werden.
- Der VectorIndex wird pro erfolgreich verarbeitetem Objekt inkrementell
  aktualisiert. `RebuildVectorIndex()` stellt einen vollständigen Rebuild bereit.

## Semantische Regeln

| Auslöser | Verhalten |
| --- | --- |
| Objektregistrierung | Embedding im Hintergrund erzeugen |
| Name, Typ, Meta oder StateDefinition geändert | Objekt dirty markieren |
| Expliziter Reindex | Objekt dirty markieren |
| Regelmäßiges State-Wertupdate | kein Embedding, kein Indexupdate |

Die Dirty-Markierung ist damit semantisch und nicht mit einer bloßen Änderung
des aktuellen State-Werts gleichzusetzen.

## Persistenzmigration

Neue Snapshots schreiben getrennte Objekt- und History-Zeilen. History-Zeilen
werden dadurch deutlich kleiner. Beim Laden werden bisherige Snapshot-Zeilen
mit einem `Embedding`-Feld toleriert, das Feld wird aber nicht in einen
`HistorySample` übernommen. Für solche legacy Objekte wird eine neue
semantische Verarbeitung eingeplant.

## Betrieb und Lifecycle

Der Worker wird nach dem Snapshot-Laden im `OnStart`-Lifecycle gestartet und im
`OnStop` vor dem finalen Snapshot gestoppt. Ausstehende dirty Objekte werden bei
geordnetem Shutdown einmalig verarbeitet. Die Worker-Instanz ist über
`MainRuntimeContext.SemanticWorker` sichtbar; für gezielte Verarbeitung stehen
`ProcessOnce`, `RequestSemanticReindex` und `RebuildVectorIndex` bereit.

## Nicht Bestandteil dieser Änderung

ONNX-Provider, HNSW-Index, priorisierte Scheduling-Strategien und verteilte
semantische Indizierung bleiben geplante Erweiterungen. Eine belastbare
Raspberry-Pi-Performance-Baseline muss mit einer Zielhardware und realistischen
State-Frequenzen separat erhoben werden.
