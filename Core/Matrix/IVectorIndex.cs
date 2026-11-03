using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Semantischer Index f�r Embeddings. Erm�glicht k-n�chste-Nachbarn
    /// Suche �ber alle registrierten States der <see cref="SemanticStateMatrix"/>.
    /// </summary>
    public interface IVectorIndex
    {
        /// <summary>
        /// F�gt (oder aktualisiert) den Embedding-Eintrag f�r die �bergebene ID.
        /// </summary>
        void Add(string id, float[] embedding);

        /// <summary>
        /// Entfernt den Embedding-Eintrag f�r die �bergebene ID.
        /// </summary>
        void Remove(string id);

        /// <summary>
        /// F�hrt eine k-n�chste-Nachbarn Suche nach dem �bergebenen Embedding durch.
        /// </summary>
        /// <returns>Liste von (Id, Score) sortiert nach absteigender �hnlichkeit.</returns>
        IReadOnlyList<(string Id, float Score)> Search(float[] queryEmbedding, int k);
    }
}
