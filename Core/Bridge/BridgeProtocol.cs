using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Framing-Schicht des TCP-Bridge-Protokolls: kompaktes JSON pro Nachricht,
    /// mit einem 4-Byte-Big-Endian-L�ngenpr�fix, damit Nachrichtengrenzen
    /// zuverl�ssig �ber den TCP-Stream erkannt werden (keine Nachrichtenfragmentierung).
    /// </summary>
    public static class BridgeProtocol
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Serialisiert ein <see cref="BridgeEnvelope"/> und schreibt es mit
        /// L�ngenpr�fix auf den �bergebenen Stream.
        /// </summary>
        public static async Task WriteAsync(NetworkStream stream, BridgeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(envelope, SerializerOptions);
            var payload = Encoding.UTF8.GetBytes(json);
            var lengthPrefix = BitConverter.GetBytes(payload.Length);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthPrefix);
            }

            await stream.WriteAsync(lengthPrefix, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// Liest ein <see cref="BridgeEnvelope"/> vom Stream (blockierend bis eine
        /// vollst�ndige Nachricht vorliegt), oder <c>null</c>, wenn die Verbindung
        /// sauber beendet wurde.
        /// </summary>
        public static async Task<BridgeEnvelope?> ReadAsync(NetworkStream stream, CancellationToken cancellationToken = default)
        {
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(stream, lengthBuffer, cancellationToken))
            {
                return null;
            }

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBuffer);
            }

            var length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length <= 0 || length > 16 * 1024 * 1024)
            {
                throw new InvalidDataException($"Ung�ltige Nachrichtenl�nge im TCP-Bridge-Protokoll: {length}");
            }

            var payloadBuffer = new byte[length];
            if (!await ReadExactAsync(stream, payloadBuffer, cancellationToken))
            {
                return null;
            }

            var json = Encoding.UTF8.GetString(payloadBuffer);
            return JsonSerializer.Deserialize<BridgeEnvelope>(json, SerializerOptions);
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }
    }
}
