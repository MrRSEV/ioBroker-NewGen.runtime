using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Thread-sicherer, voll funktionsf�higer semantischer Index auf Basis
    /// von Brute-Force-Kosinus-�hnlichkeit. F�r die erwarteten Datenmengen
    /// einer Smarthome-Installation (zehntausende States) ausreichend performant;
    /// kann sp�ter durch eine echte ANN-Struktur (z. B. HNSW) ersetzt werden,
    /// ohne dass Aufrufer (<see cref="SemanticStateMatrix"/>) angepasst werden m�ssen.
    /// </summary>
    public sealed class VectorIndex : IVectorIndex
    {
        private readonly ConcurrentDictionary<string, float[]> _vectors = new();

        public void Add(string id, float[] embedding)
        {
            _vectors[id] = embedding;
        }

        public void Remove(string id)
        {
            _vectors.TryRemove(id, out _);
        }

        public IReadOnlyList<(string Id, float Score)> Search(float[] queryEmbedding, int k)
        {
            if (k <= 0)
            {
                return Array.Empty<(string, float)>();
            }

            return _vectors
                .Select(kvp => (Id: kvp.Key, Score: CosineSimilarity(queryEmbedding, kvp.Value)))
                .OrderByDescending(entry => entry.Score)
                .Take(k)
                .ToList();
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            var length = Math.Min(a.Length, b.Length);
            if (length == 0)
            {
                return 0f;
            }

            double dot = 0, magnitudeA = 0, magnitudeB = 0;
            for (var i = 0; i < length; i++)
            {
                dot += a[i] * b[i];
                magnitudeA += a[i] * a[i];
                magnitudeB += b[i] * b[i];
            }

            if (magnitudeA <= 0 || magnitudeB <= 0)
            {
                return 0f;
            }

            return (float)(dot / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB)));
        }
    }
}
