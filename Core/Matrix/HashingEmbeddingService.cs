using System;
using System.Security.Cryptography;
using System.Text;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Deterministische, abh�ngigkeitsfreie Embedding-Implementierung auf
    /// Basis von Hashing (Feature Hashing / "Hashing Trick"). Erzeugt f�r
    /// identischen Text immer denselben, L2-normierten Vektor. Dient als
    /// voll funktionsf�higer Platzhalter, bis ein echtes ML-Modell integriert wird.
    /// </summary>
    public sealed class HashingEmbeddingService : IEmbeddingService
    {
        public int Dimensions { get; }

        public HashingEmbeddingService(int dimensions = 128)
        {
            if (dimensions <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensionen m�ssen gr��er als 0 sein.");
            }

            Dimensions = dimensions;
        }

        public float[] Encode(string text)
        {
            var vector = new float[Dimensions];
            if (string.IsNullOrEmpty(text))
            {
                return vector;
            }

            var normalized = text.ToLowerInvariant();
            var tokens = normalized.Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ':', ';', '/', '\\', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                tokens = new[] { normalized };
            }

            foreach (var token in tokens)
            {
                var hash = StableHash(token);
                var bucket = (int)(hash % (uint)Dimensions);
                var sign = (hash & 1) == 0 ? 1f : -1f;
                vector[bucket] += sign;
            }

            Normalize(vector);
            return vector;
        }

        private static uint StableHash(string token)
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var hashBytes = MD5.HashData(bytes);
            return BitConverter.ToUInt32(hashBytes, 0);
        }

        private static void Normalize(float[] vector)
        {
            double sumSquares = 0;
            foreach (var component in vector)
            {
                sumSquares += component * component;
            }

            if (sumSquares <= 0)
            {
                return;
            }

            var magnitude = (float)Math.Sqrt(sumSquares);
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= magnitude;
            }
        }
    }
}
