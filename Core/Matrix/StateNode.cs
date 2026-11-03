using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Aktueller Zustand eines <see cref="MatrixObject"/> inklusive
    /// semantischer Informationen (Embedding) und History.
    /// Thread-sicher f�r nebenl�ufige Lese-/Schreibzugriffe.
    /// </summary>
    public sealed class StateNode
    {
        private readonly object _valueLock = new();
        private readonly int _maxInMemoryHistory;
        private object? _value;
        private DateTime _timestamp;
        private string? _lastWrittenBy;
        private float[] _embedding = Array.Empty<float>();

        /// <summary>
        /// Eindeutige ID des Zustands (identisch mit der zugeh�rigen <see cref="MatrixObject.Id"/>).
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Aktueller Wert des Zustands.
        /// </summary>
        public object? Value
        {
            get
            {
                lock (_valueLock)
                {
                    return _value;
                }
            }
        }

        /// <summary>
        /// Zeitstempel der letzten Aktualisierung (UTC).
        /// </summary>
        public DateTime Timestamp
        {
            get
            {
                lock (_valueLock)
                {
                    return _timestamp;
                }
            }
        }

        /// <summary>
        /// Letzter Benutzer/Adapter, der den Wert gesetzt hat.
        /// </summary>
        public string? LastWrittenBy
        {
            get
            {
                lock (_valueLock)
                {
                    return _lastWrittenBy;
                }
            }
        }

        /// <summary>
        /// Semantisches Embedding des aktuellen Payloads.
        /// </summary>
        public float[] Embedding
        {
            get
            {
                lock (_valueLock)
                {
                    return _embedding.ToArray();
                }
            }
        }

        /// <summary>
        /// Beliebige zus�tzliche Metadaten (Rolle, Einheit, Typ etc.).
        /// </summary>
        public Dictionary<string, string> Meta { get; init; } = new();

        /// <summary>
        /// In-Memory-Verlauf der letzten Werte (begrenzt). Die vollst�ndige
        /// History wird �ber die Snapshot-Datei persistiert.
        /// </summary>
        public ConcurrentQueue<HistorySample> History { get; } = new();

        public StateNode(string id, int maxInMemoryHistory = 500)
        {
            Id = id;
            _maxInMemoryHistory = maxInMemoryHistory;
            _timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Aktualisiert Wert, Zeitstempel, Embedding und History thread-sicher.
        /// </summary>
        internal HistorySample Update(
            object? value,
            float[] embedding,
            string? writtenBy,
            DateTime? timestamp = null)
        {
            lock (_valueLock)
            {
                _value = value;
                _embedding = embedding?.ToArray() ?? Array.Empty<float>();
                _timestamp = timestamp ?? DateTime.UtcNow;
                _lastWrittenBy = writtenBy;

                var sample = new HistorySample(Id, _timestamp, value, _embedding.ToArray());
                History.Enqueue(sample);

                while (History.Count > _maxInMemoryHistory && History.TryDequeue(out _))
                {
                    // �lteste In-Memory-Eintr�ge verwerfen; die vollst�ndige
                    // History bleibt �ber den Snapshot erhalten.
                }

                return sample;
            }
        }

        /// <summary>
        /// Stellt einen State aus einem geladenen Snapshot wieder her,
        /// ohne eine neue History-Sample-Erzeugung auszul�sen.
        /// </summary>
        internal void Restore(object? value, DateTime timestamp, float[] embedding, string? writtenBy)
        {
            lock (_valueLock)
            {
                _value = value;
                _timestamp = timestamp;
                _embedding = embedding?.ToArray() ?? Array.Empty<float>();
                _lastWrittenBy = writtenBy;
            }
        }

        /// <summary>
        /// F�gt eine aus dem Snapshot rekonstruierte History-Sample direkt hinzu,
        /// ohne den aktuellen Wert zu ver�ndern.
        /// </summary>
        internal void AppendHistorySample(HistorySample sample)
        {
            History.Enqueue(sample);
            while (History.Count > _maxInMemoryHistory && History.TryDequeue(out _))
            {
            }
        }
    }
}
