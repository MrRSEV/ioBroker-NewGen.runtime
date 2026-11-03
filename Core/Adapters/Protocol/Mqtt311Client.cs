using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    /// <summary>
    /// Kleiner MQTT-3.1.1-Client für QoS 0. Er unterstützt Connect, Subscribe,
    /// Publish, Ping und sauberes Disconnect ohne externe NuGet-Abhängigkeit.
    /// </summary>
    public sealed class Mqtt311Client : IMqttClient
    {
        private readonly MqttClientOptions _options;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private TcpClient? _tcpClient;
        private Stream? _stream;
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private CancellationTokenSource? _keepAliveCts;
        private Task? _keepAliveTask;
        private ushort _packetId;

        public Mqtt311Client(MqttClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public bool IsConnected { get; private set; }

        public event Func<string, ReadOnlyMemory<byte>, Task>? MessageReceived;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (IsConnected)
            {
                return;
            }

            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_options.Host, _options.Port, cancellationToken);
            _stream = _tcpClient.GetStream();

            if (_options.UseTls)
            {
                var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _options.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, cancellationToken);
                _stream = ssl;
            }

            var connectPayload = BuildConnectPayload();
            await WritePacketAsync(0x10, connectPayload, cancellationToken);
            var response = await ReadPacketAsync(cancellationToken);
            if (response is null || response.Value.Header >> 4 != 2 || response.Value.Payload.Length < 2 || response.Value.Payload[1] != 0)
            {
                throw new InvalidDataException("MQTT-CONNACK wurde abgelehnt.");
            }

            IsConnected = true;
        }

        public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var packetId = NextPacketId();
            var filter = EncodeUtf8(topicFilter);
            var payload = new byte[2 + filter.Length + 1];
            WriteUInt16(payload, 0, packetId);
            Buffer.BlockCopy(filter, 0, payload, 2, filter.Length);
            payload[^1] = 0;

            await WritePacketAsync(0x82, payload, cancellationToken);
            var response = await ReadPacketAsync(cancellationToken);
            if (response is null || response.Value.Header >> 4 != 9 || response.Value.Payload.Length < 3 || response.Value.Payload[0] != (byte)(packetId >> 8) || response.Value.Payload[1] != (byte)packetId)
            {
                throw new InvalidDataException("MQTT-SUBACK fehlt oder ist ungültig.");
            }

            if (_receiveTask is null)
            {
                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
                if (_options.KeepAliveSeconds > 0)
                {
                    _keepAliveCts = new CancellationTokenSource();
                    _keepAliveTask = KeepAliveLoopAsync(_keepAliveCts.Token);
                }
            }
        }

        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, bool retain = false, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            var topicBytes = Encoding.UTF8.GetBytes(topic);
            var packetPayload = new byte[2 + topicBytes.Length + payload.Length];
            WriteUInt16(packetPayload, 0, (ushort)topicBytes.Length);
            Buffer.BlockCopy(topicBytes, 0, packetPayload, 2, topicBytes.Length);
            payload.CopyTo(packetPayload.AsMemory(2 + topicBytes.Length));
            var header = (byte)0x30;
            if (retain)
            {
                header |= 0x01;
            }

            return WritePacketAsync(header, packetPayload, cancellationToken);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_stream is not null && IsConnected)
            {
                try
                {
                    await WritePacketAsync(0xE0, Array.Empty<byte>(), cancellationToken);
                }
                catch (IOException)
                {
                }
            }

            IsConnected = false;
            _keepAliveCts?.Cancel();
            _receiveCts?.Cancel();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;

            if (_keepAliveTask is not null)
            {
                try
                {
                    await _keepAliveTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _keepAliveTask = null;
            _keepAliveCts?.Dispose();
            _keepAliveCts = null;

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
            }

            _receiveTask = null;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _receiveCts?.Dispose();
            _writeLock.Dispose();
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _stream is not null)
                {
                    var packet = await ReadPacketAsync(cancellationToken);
                    if (packet is null)
                    {
                        break;
                    }

                    var packetType = packet.Value.Header >> 4;
                    if (packetType == 3)
                    {
                        var (topic, payload) = ParsePublish(packet.Value.Header, packet.Value.Payload);
                        var handler = MessageReceived;
                        if (handler is not null)
                        {
                            await handler(topic, payload);
                        }
                    }
                    else if (packetType == 12)
                    {
                        await WritePacketAsync(0xD0, Array.Empty<byte>(), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                IsConnected = false;
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.KeepAliveSeconds / 2d));
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    if (!IsConnected)
                    {
                        return;
                    }

                    await WritePacketAsync(0xC0, Array.Empty<byte>(), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Der ReceiveLoop erkennt den Verbindungsabbruch und setzt IsConnected zurück.
            }
        }

        private async Task WritePacketAsync(byte header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                throw new InvalidOperationException("MQTT-Verbindung ist nicht geöffnet.");
            }

            var remainingLength = EncodeRemainingLength(payload.Length);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _stream.WriteAsync(new[] { header }, cancellationToken);
                await _stream.WriteAsync(remainingLength, cancellationToken);
                await _stream.WriteAsync(payload, cancellationToken);
                await _stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<MqttPacket?> ReadPacketAsync(CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                return null;
            }

            var first = await ReadExactAsync(1, cancellationToken);
            if (first is null)
            {
                return null;
            }

            var multiplier = 1;
            var remainingLength = 0;
            for (var index = 0; index < 4; index++)
            {
                var encoded = await ReadExactAsync(1, cancellationToken);
                if (encoded is null)
                {
                    return null;
                }

                var value = encoded[0];
                remainingLength += (value & 127) * multiplier;
                if ((value & 128) == 0)
                {
                    break;
                }

                multiplier *= 128;
                if (index == 3)
                {
                    throw new InvalidDataException("Ungültige MQTT-Restlänge.");
                }
            }

            var payload = await ReadExactAsync(remainingLength, cancellationToken) ?? Array.Empty<byte>();
            return new MqttPacket(first[0], payload);
        }

        private async Task<byte[]?> ReadExactAsync(int length, CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                return null;
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            return buffer;
        }

        private byte[] BuildConnectPayload()
        {
            var protocol = EncodeUtf8("MQTT");
            var clientId = EncodeUtf8(_options.ClientId);
            var username = _options.Username is null ? Array.Empty<byte>() : EncodeUtf8(_options.Username);
            var password = _options.Password is null ? Array.Empty<byte>() : EncodeUtf8(_options.Password);
            var flags = (byte)0x02;
            if (_options.Username is not null)
            {
                flags |= 0x80;
            }

            if (_options.Password is not null)
            {
                flags |= 0x40;
            }

            var payload = new List<byte>();
            payload.AddRange(protocol);
            payload.Add(4);
            payload.Add(flags);
            payload.Add((byte)(_options.KeepAliveSeconds >> 8));
            payload.Add((byte)_options.KeepAliveSeconds);
            payload.AddRange(clientId);
            if (_options.Username is not null)
            {
                payload.AddRange(username);
            }

            if (_options.Password is not null)
            {
                payload.AddRange(password);
            }

            return payload.ToArray();
        }

        private static (string Topic, ReadOnlyMemory<byte> Payload) ParsePublish(byte header, byte[] payload)
        {
            if (payload.Length < 2)
            {
                throw new InvalidDataException("MQTT-PUBLISH ohne Topic.");
            }

            var topicLength = (payload[0] << 8) | payload[1];
            var offset = 2;
            if (topicLength <= 0 || offset + topicLength > payload.Length)
            {
                throw new InvalidDataException("Ungültiges MQTT-PUBLISH-Topic.");
            }

            var topic = Encoding.UTF8.GetString(payload, offset, topicLength);
            offset += topicLength;
            var qos = (header >> 1) & 3;
            if (qos > 0)
            {
                if (offset + 2 > payload.Length)
                {
                    throw new InvalidDataException("MQTT-PUBLISH ohne Packet-ID.");
                }

                offset += 2;
            }

            return (topic, payload.AsMemory(offset));
        }

        private void EnsureConnected()
        {
            if (!IsConnected || _stream is null)
            {
                throw new InvalidOperationException("MQTT-Verbindung ist nicht geöffnet.");
            }
        }

        private ushort NextPacketId()
        {
            _packetId++;
            if (_packetId == 0)
            {
                _packetId = 1;
            }

            return _packetId;
        }

        private static byte[] EncodeUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var result = new byte[2 + bytes.Length];
            WriteUInt16(result, 0, (ushort)bytes.Length);
            Buffer.BlockCopy(bytes, 0, result, 2, bytes.Length);
            return result;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static byte[] EncodeRemainingLength(int length)
        {
            var result = new List<byte>();
            do
            {
                var encoded = length % 128;
                length /= 128;
                if (length > 0)
                {
                    encoded |= 128;
                }

                result.Add((byte)encoded);
            }
            while (length > 0);

            return result.ToArray();
        }

        private readonly record struct MqttPacket(byte Header, byte[] Payload);
    }
}
