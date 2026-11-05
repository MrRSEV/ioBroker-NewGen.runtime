using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Aktueller Zustand eines MatrixObject inklusive Wert und History.
    /// Semantische Embeddings werden am MatrixObject gehalten.
    /// </summary>
    public sealed class StateNode
    {
        private readonly object _valueLock = new();
        private readonly int _maxInMemoryHistory;
        private object? _value;
        private DateTime _timestamp;
        private string? _lastWrittenBy;

        public string Id { get; }

        public object? Value
        {
            get { lock (_valueLock) return _value; }
        }

        public DateTime Timestamp
        {
            get { lock (_valueLock) return _timestamp; }
        }

        public string? LastWrittenBy
        {
            get { lock (_valueLock) return _lastWrittenBy; }
        }

        /// <summary>Zusätzliche State-Metadaten (Rolle, Einheit, Typ etc.).</summary>
        public Dictionary<string, string> Meta { get; init; } = new();

        /// <summary>Begrenzter In-Memory-Verlauf der Werte.</summary>
        public ConcurrentQueue<HistorySample> History { get; } = new();

        public StateNode(string id, int maxInMemoryHistory = 500)
        {
            Id = id;
            _maxInMemoryHistory = maxInMemoryHistory;
            _timestamp = DateTime.UtcNow;
        }

        /// <summary>Aktualisiert Wert, Zeitstempel und History thread-sicher.</summary>
        internal HistorySample Update(object? value, string? writtenBy, DateTime? timestamp = null)
        {
            lock (_valueLock)
            {
                _value = value;
                _timestamp = timestamp ?? DateTime.UtcNow;
                _lastWrittenBy = writtenBy;

                var sample = new HistorySample(Id, _timestamp, value);
                History.Enqueue(sample);
                while (History.Count > _maxInMemoryHistory && History.TryDequeue(out _))
                {
                    // Älteste In-Memory-Einträge verwerfen.
                }

                return sample;
            }
        }

        /// <summary>
        /// Stellt einen State aus einem Snapshot wieder her, ohne einen neuen
        /// History-Eintrag zu erzeugen.
        /// </summary>
        internal void Restore(object? value, DateTime timestamp, string? writtenBy)
        {
            lock (_valueLock)
            {
                _value = value;
                _timestamp = timestamp;
                _lastWrittenBy = writtenBy;
            }
        }

        internal void AppendHistorySample(HistorySample sample)
        {
            History.Enqueue(sample);
            while (History.Count > _maxInMemoryHistory && History.TryDequeue(out _))
            {
            }
        }
    }
}
