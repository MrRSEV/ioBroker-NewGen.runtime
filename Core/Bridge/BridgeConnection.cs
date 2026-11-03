using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Repr�sentiert eine einzelne, verbundene Adapter-Sitzung der TCP-Bridge.
    /// Kapselt den <see cref="TcpClient"/>, die zugeh�rige Adapter-ID (nach Handshake)
    /// sowie eine einfache Sende-Serialisierung (ein Writer gleichzeitig pro Verbindung).
    /// </summary>
    public sealed class BridgeConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        /// <summary>
        /// Die vom Host vergebene Adapter-ID (Format "Name.id"), sobald der
        /// Handshake abgeschlossen ist. Vor dem Handshake <c>null</c>.
        /// </summary>
        public string? AdapterId { get; set; }

        /// <summary>
        /// Zeitpunkt des letzten empfangenen Lebenszeichens (Handshake, Frame oder Heartbeat).
        /// </summary>
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        public BridgeConnection(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _stream = client.GetStream();
        }

        /// <summary>
        /// Liest das n�chste Envelope von der Verbindung (oder <c>null</c> bei Verbindungsende).
        /// </summary>
        public Task<BridgeEnvelope?> ReadAsync(CancellationToken cancellationToken = default)
        {
            return BridgeProtocol.ReadAsync(_stream, cancellationToken);
        }

        /// <summary>
        /// Sendet ein Envelope thread-sicher (serialisiert gegen parallele Schreibzugriffe).
        /// </summary>
        public async Task SendAsync(BridgeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await BridgeProtocol.WriteAsync(_stream, envelope, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _stream.Close();
            }
            catch
            {
                // Verbindung ggf. bereits beendet � ignorieren.
            }

            _client.Close();
            _writeLock.Dispose();
            await Task.CompletedTask;
        }
    }
}
