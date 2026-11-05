using System;
using System.Collections.Generic;
using System.Linq;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Beschreibt ein Gerät, einen Kanal oder einen Datenpunkt innerhalb der
    /// <see cref="SemanticStateMatrix"/>. Das semantische Embedding gehört zum
    /// Objekt und nicht zu einzelnen Wert-/History-Samples.
    /// </summary>
    public sealed class MatrixObject
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        public Dictionary<string, string> Meta { get; init; } = new();
        public StateDefinition? StateDefinition { get; init; }

        /// <summary>Semantisches Embedding des Objekts.</summary>
        public float[] Embedding { get; internal set; } = Array.Empty<float>();

        /// <summary>
        /// Zeitpunkt der letzten erfolgreichen semantischen Aktualisierung.
        /// DateTime.MinValue bedeutet, dass noch kein Embedding persistiert wurde.
        /// </summary>
        public DateTime LastEmbeddingUpdate { get; internal set; }

        /// <summary>
        /// Kennzeichnet eine ausstehende semantische Verarbeitung.
        /// </summary>
        public bool SemanticDirty { get; internal set; } = true;

        internal long SemanticVersion { get; set; } = 1;

        internal void MarkSemanticDirty()
        {
            SemanticDirty = true;
            SemanticVersion++;
        }

        internal void StoreEmbedding(float[] embedding, DateTime updatedAt)
        {
            Embedding = embedding?.ToArray() ?? Array.Empty<float>();
            LastEmbeddingUpdate = updatedAt;
            SemanticDirty = false;
        }

        /// <summary>
        /// Baut den Text für das semantische Objekt-Embedding aus Id, Name,
        /// Typ und deterministisch sortierten Metadaten.
        /// </summary>
        public string BuildEmbeddingSourceText()
        {
            var metaText = string.Join(' ', Meta.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            return $"{Id} {Name} {Type} {metaText}".Trim();
        }
    }
}
