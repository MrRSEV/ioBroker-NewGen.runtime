using System.Collections.Generic;
using System.Linq;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Beschreibt ein Ger�t, einen Kanal oder einen Datenpunkt innerhalb der
    /// <see cref="SemanticStateMatrix"/>. Dient als Identit�tstr�ger f�r
    /// einen oder mehrere <see cref="StateNode"/>-Eintr�ge.
    /// </summary>
    public sealed class MatrixObject
    {
        /// <summary>
        /// Eindeutige ID des Objekts (z. B. "temp.livingroom").
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Sprechender Name des Objekts.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Typ des Objekts (z. B. "device", "channel", "state").
        /// </summary>
        public required string Type { get; init; }

        /// <summary>
        /// Beliebige zus�tzliche Metadaten (Rolle, Einheit, Hersteller etc.).
        /// </summary>
        public Dictionary<string, string> Meta { get; init; } = new();

        /// <summary>
        /// Optionale Validierungs- und Semantikdefinition des zugehörigen States.
        /// </summary>
        public StateDefinition? StateDefinition { get; init; }

        /// <summary>
        /// Baut den Text zusammen, der als Grundlage f�r das semantische
        /// Objekt-Embedding verwendet wird (Id + Name + Type + Meta).
        /// </summary>
        public string BuildEmbeddingSourceText()
        {
            var metaText = string.Join(' ', Meta.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            return $"{Id} {Name} {Type} {metaText}".Trim();
        }
    }
}
