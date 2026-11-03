using System;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Datei-basiertes JSONL-Format (eine History-Sample pro Zeile) f�r den
    /// Snapshot-Mechanismus der <see cref="SemanticStateMatrix"/>.
    /// Append-only, kompakt, schnell zu laden � siehe Spezifikation "SnapshotFormat".
    /// </summary>
    internal sealed class SnapshotEntry
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public object? Value { get; set; }
        public float[] Embedding { get; set; } = Array.Empty<float>();

        /// <summary>
        /// Wird nur f�r den jeweils letzten Eintrag eines States beim Laden
        /// verwendet, um Meta-Informationen des zugeh�rigen Objekts zu bewahren.
        /// </summary>
        public string? Name { get; set; }
        public string? Type { get; set; }
    }
}
