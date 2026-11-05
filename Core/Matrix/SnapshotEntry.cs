using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// JSONL-Snapshotformat. Neben History-Zeilen werden semantische
    /// Objekt-Zeilen geschrieben, damit Embeddings nicht in jedem Wert-Sample
    /// dupliziert werden.
    /// </summary>
    internal sealed class SnapshotEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = "history";
        public DateTime Timestamp { get; set; }
        public object? Value { get; set; }

        /// <summary>Nur für Kind = "object"; History-Zeilen setzen es nicht.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Embedding { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastEmbeddingUpdate { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? SemanticDirty { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Meta { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StateDefinition? StateDefinition { get; set; }

        /// <summary>
        /// Legacy-History-Snapshots führten Name und Type pro Zeile mit.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }
    }
}
