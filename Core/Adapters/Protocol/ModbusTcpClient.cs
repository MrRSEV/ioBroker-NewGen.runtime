using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    /// <summary>
    /// Kleiner Modbus-TCP-Client für Funktionen 03 (Holding Register) und 06
    /// (Single Register). Er hält nur eine serielle Anfrage pro Verbindung.
    /// </summary>
    public sealed class ModbusTcpClient : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private TcpClient? _client;
        private NetworkStream? _stream;
        private ushort _transactionId;

        public ModbusTcpClient(string host, int port = 502)
        {
            _host = string.IsNullOrWhiteSpace(host) ? throw new ArgumentException("Modbus-Host fehlt.", nameof(host)) : host;
            _port = port is < 1 or > 65535 ? throw new ArgumentOutOfRangeException(nameof(port)) : port;
        }

        public bool IsConnected => _client?.Connected == true && _stream is not null;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                return;
            }

            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, cancellationToken);
            _stream = _client.GetStream();
        }

        public async Task<ushort[]> ReadHoldingRegistersAsync(
            byte unitId,
            ushort startAddress,
            ushort quantity,
            CancellationToken cancellationToken = default)
        {
            if (quantity is < 1 or > 125)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Modbus erlaubt 1 bis 125 Register pro Anfrage.");
            }

            var payload = new byte[6];
            payload[0] = 3;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), quantity);
            payload[5] = 0;
            return await ExecuteAsync(unitId, payload.AsMemory(0, 5), quantity, cancellationToken);
        }

        public async Task WriteSingleRegisterAsync(
            byte unitId,
            ushort address,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            var payload = new byte[5];
            payload[0] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), address);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), value);
            await ExecuteAsync(unitId, payload, expectedRegisterCount: 0, cancellationToken);
        }

        private async Task<ushort[]> ExecuteAsync(
            byte unitId,
            ReadOnlyMemory<byte> pdu,
            int expectedRegisterCount,
            CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new InvalidOperationException("Modbus-Verbindung ist nicht geöffnet.");
            }

            await _ioLock.WaitAsync(cancellationToken);
            try
            {
                var transaction = unchecked(++_transactionId);
                var request = new byte[7 + pdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transaction);
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(1 + pdu.Length));
                request[6] = unitId;
                pdu.CopyTo(request.AsMemory(7));
                await _stream.WriteAsync(request, cancellationToken);
                await _stream.FlushAsync(cancellationToken);

                var header = await ReadExactAsync(7, cancellationToken);
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0)) != transaction ||
                    BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 ||
                    header[6] != unitId)
                {
                    throw new InvalidDataException("Ungültige Modbus-TCP-Transaktion.");
                }

                var remaining = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (remaining < 2)
                {
                    throw new InvalidDataException("Ungültige Modbus-TCP-Länge.");
                }

                var body = await ReadExactAsync(remaining - 1, cancellationToken);
                if ((body[0] & 0x80) != 0)
                {
                    throw new IOException($"Modbus-Exception {(body.Length > 1 ? body[1] : (byte)0)}.");
                }

                if (body[0] == 3)
                {
                    var byteCount = body.Length > 1 ? body[1] : 0;
                    if (byteCount != expectedRegisterCount * 2 || body.Length < byteCount + 2)
                    {
                        throw new InvalidDataException("Ungültige Modbus-Registerantwort.");
                    }

                    var result = new ushort[expectedRegisterCount];
                    for (var index = 0; index < result.Length; index++)
                    {
                        result[index] = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + index * 2));
                    }

                    return result;
                }

                if (body[0] == 6 && body.Length >= 5)
                {
                    return Array.Empty<ushort>();
                }

                throw new InvalidDataException($"Unerwartete Modbus-Funktion {body[0]}.");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new InvalidOperationException("Modbus-Verbindung ist nicht geöffnet.");
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("Modbus-Verbindung wurde beendet.");
                }

                offset += read;
            }

            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
            _ioLock.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
