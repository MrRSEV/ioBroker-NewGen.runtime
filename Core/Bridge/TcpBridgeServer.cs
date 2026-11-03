using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// TCP-Bridge-Server f�r isolierte Legacy-ioBroker-Adapter (Node.js-Prozesse).
    /// Lauscht auf einem festen Host-Port, f�hrt den Handshake durch (Adapter sendet
    /// Namen, Host vergibt ID im Format "Name.id") und transportiert anschlie�end
    /// <see cref="BusFrame"/>-Payloads bidirektional zwischen Adapter und Systembus.
    /// </summary>
    public sealed class TcpBridgeServer : IAsyncDisposable
    {
        private readonly int _port;
        private readonly IPAddress _bindAddress;
        private readonly AdapterRegistry _adapterRegistry;
        private readonly SystemMessageBus _systemBus;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, BridgeConnection> _connectionsByAdapterId = new();

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoopTask;
        private Timer? _heartbeatTimer;
        private readonly TimeSpan _heartbeatTimeout;

        public TcpBridgeServer(int port, AdapterRegistry adapterRegistry, SystemMessageBus systemBus, ILogger logger)
            : this(port, IPAddress.Loopback, adapterRegistry, systemBus, logger)
        {
        }

        public TcpBridgeServer(
            int port,
            string bindHost,
            AdapterRegistry adapterRegistry,
            SystemMessageBus systemBus,
            ILogger logger,
            TimeSpan? heartbeatTimeout = null)
            : this(
                port,
                ResolveBindAddress(bindHost),
                adapterRegistry,
                systemBus,
                logger,
                heartbeatTimeout)
        {
        }

        private TcpBridgeServer(
            int port,
            IPAddress bindAddress,
            AdapterRegistry adapterRegistry,
            SystemMessageBus systemBus,
            ILogger logger,
            TimeSpan? heartbeatTimeout = null)
        {
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port muss zwischen 1 und 65535 liegen.");
            }

            _port = port;
            _bindAddress = bindAddress;
            _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
            _systemBus = systemBus ?? throw new ArgumentNullException(nameof(systemBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Startet den TCP-Listener und beginnt, eingehende Adapter-Verbindungen anzunehmen.
        /// </summary>
        public Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_bindAddress, _port);
            _listener.Start();

            _logger.LogInfo($"[TcpBridge] Lauscht auf {_bindAddress}:{_port}.");

            _acceptLoopTask = AcceptLoopAsync(_cts.Token);
            _heartbeatTimer = new Timer(
                _ => DisconnectStaleConnections(),
                state: null,
                dueTime: _heartbeatTimeout,
                period: TimeSpan.FromSeconds(Math.Max(1, _heartbeatTimeout.TotalSeconds / 2)));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stoppt den Listener und schlie�t alle aktiven Verbindungen.
        /// </summary>
        public async Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;

            if (_acceptLoopTask is not null)
            {
                try
                {
                    await _acceptLoopTask;
                }
                catch (OperationCanceledException)
                {
                    // Erwartet beim Herunterfahren.
                }
            }

            foreach (var connection in _connectionsByAdapterId.Values.ToList())
            {
                await connection.DisposeAsync();
            }

            _connectionsByAdapterId.Clear();
            _logger.LogInfo("[TcpBridge] Gestoppt.");
        }

        /// <summary>
        /// Sendet ein Frame an einen konkreten, verbundenen ioBroker-Adapter
        /// (z. B. Befehl vom Systembus Richtung Legacy-Adapter).
        /// </summary>
        public async Task<bool> SendToAdapterAsync(string adapterId, BusFrame frame)
        {
            if (!_connectionsByAdapterId.TryGetValue(adapterId, out var connection))
            {
                _logger.LogWarning($"[TcpBridge] Kein verbundener Adapter mit ID '{adapterId}' gefunden.");
                return false;
            }

            var envelope = new BridgeEnvelope
            {
                Type = BridgeMessageType.Frame,
                AdapterId = frame.AdapterId,
                Address = frame.Address,
                ValueType = frame.ValueType,
                FrameKind = (int)frame.Kind,
                PayloadBase64 = Convert.ToBase64String(frame.Payload.Span),
                Metadata = frame.Metadata.Values,
                Timestamp = frame.Timestamp
            };

            await connection.SendAsync(envelope);
            return true;
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleConnectionAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Erwartet beim Herunterfahren.
            }
            catch (ObjectDisposedException)
            {
                // Listener wurde gestoppt.
            }
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var connection = new BridgeConnection(client);
            string? adapterId = null;

            try
            {
                // Handshake: Erwartet die erste Nachricht als BridgeMessageType.Handshake mit dem Adapternamen.
                var handshake = await connection.ReadAsync(cancellationToken);
                if (handshake is null || handshake.Type != BridgeMessageType.Handshake || string.IsNullOrWhiteSpace(handshake.AdapterId))
                {
                    _logger.LogWarning("[TcpBridge] Ung�ltiger oder fehlender Handshake � Verbindung wird getrennt.");
                    await connection.DisposeAsync();
                    return;
                }

                var adapterName = handshake.AdapterId;
                var ioBrokerAdapters = _adapterRegistry.GetByType(AdapterType.IoBroker);
                var candidates = ioBrokerAdapters
                    .Where(candidate => string.Equals(candidate.Name, adapterName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .ToArray();
                // Legacy-Adapter melden ihren Namespace ("iot.1") als Namen: dann ist die Instanz eindeutig.
                var registration = ioBrokerAdapters.FirstOrDefault(candidate =>
                                      string.Equals(candidate.Id, adapterName, StringComparison.OrdinalIgnoreCase))
                                  ?? candidates.FirstOrDefault(candidate =>
                                      string.IsNullOrWhiteSpace(candidate.TcpEndpoint) ||
                                      candidate.Status is AdapterStatus.Disconnected or AdapterStatus.Faulted)
                                  ?? (candidates.Length == 1 ? candidates[0] : null)
                                  ?? _adapterRegistry.Register(adapterName, AdapterType.IoBroker);

                _connectionsByAdapterId.TryGetValue(registration.Id, out var previousConnection);
                _connectionsByAdapterId[registration.Id] = connection;
                if (previousConnection is not null && !ReferenceEquals(previousConnection, connection))
                {
                    await previousConnection.DisposeAsync();
                }

                adapterId = registration.Id;
                registration.TcpEndpoint = client.Client.RemoteEndPoint?.ToString();
                _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Connected);

                connection.AdapterId = adapterId;

                await connection.SendAsync(new BridgeEnvelope
                {
                    Type = BridgeMessageType.HandshakeAck,
                    AdapterId = adapterId
                }, cancellationToken);

                _logger.LogInfo($"[TcpBridge] Adapter '{adapterName}' verbunden, ID vergeben: '{adapterId}'.");

                await ReceiveLoopAsync(connection, adapterId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug($"[TcpBridge] Verbindung von Adapter '{adapterId ?? "unbekannt"}' beim Herunterfahren beendet.");
            }
            catch (IOException ex)
            {
                _logger.LogDebug($"[TcpBridge] Adapter '{adapterId ?? "unbekannt"}' hat die Verbindung geschlossen: {ex.Message}");
            }
            catch (SocketException ex)
            {
                _logger.LogDebug($"[TcpBridge] Netzwerkverbindung von Adapter '{adapterId ?? "unbekannt"}' beendet: {ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug($"[TcpBridge] Verbindung von Adapter '{adapterId ?? "unbekannt"}' wurde geschlossen.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[TcpBridge] Fehler in Verbindung (Adapter '{adapterId ?? "unbekannt"}'): {ex.Message}");
            }
            finally
            {
                if (adapterId is not null)
                {
                    var ownsCurrentConnection = _connectionsByAdapterId.TryGetValue(adapterId, out var currentConnection) &&
                                                ReferenceEquals(currentConnection, connection);
                    if (ownsCurrentConnection)
                    {
                        _connectionsByAdapterId.TryRemove(adapterId, out _);
                        if (_adapterRegistry.Get(adapterId) is { } registration)
                        {
                            // Ein Neustart desselben Legacy-Prozesses soll seine
                            // bestehende Name.id-Identität wiederverwenden.
                            registration.TcpEndpoint = null;
                            _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Disconnected);
                        }
                    }
                    _logger.LogInfo($"[TcpBridge] Adapter '{adapterId}' getrennt.");
                }

                await connection.DisposeAsync();
            }
        }

        private async Task ReceiveLoopAsync(BridgeConnection connection, string adapterId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await connection.ReadAsync(cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                connection.LastSeenAt = DateTime.UtcNow;
                _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Connected);

                switch (envelope.Type)
                {
                    case BridgeMessageType.Frame:
                        HandleIncomingFrame(adapterId, envelope);
                        break;

                    case BridgeMessageType.Heartbeat:
                        // Lebenszeichen wurde bereits �ber LastSeenAt/UpdateStatus verarbeitet.
                        break;

                    case BridgeMessageType.Error:
                        _logger.LogWarning($"[TcpBridge] Fehlermeldung von Adapter '{adapterId}': {envelope.ErrorMessage}");
                        break;

                    default:
                        _logger.LogWarning($"[TcpBridge] Unerwarteter Nachrichtentyp '{envelope.Type}' von Adapter '{adapterId}'.");
                        break;
                }
            }
        }

        private void HandleIncomingFrame(string adapterId, BridgeEnvelope envelope)
        {
            if (string.IsNullOrEmpty(envelope.Address))
            {
                _logger.LogWarning($"[TcpBridge] Frame von Adapter '{adapterId}' ohne Adresse empfangen � wird verworfen.");
                return;
            }

            var payload = string.IsNullOrEmpty(envelope.PayloadBase64)
                ? ReadOnlyMemory<byte>.Empty
                : Convert.FromBase64String(envelope.PayloadBase64);

            var metadata = new BusFrameMetadata { Values = envelope.Metadata ?? new() };

            var frame = new BusFrame(
                adapterId,
                envelope.Address,
                payload,
                envelope.ValueType ?? "raw",
                (BusFrameKind)envelope.FrameKind,
                envelope.Timestamp,
                metadata);

            // Externe Payloads werden gem�� Architektur 1:1 auf den Systembus gespiegelt.
            _systemBus.Publish(frame);
        }

        private void DisconnectStaleConnections()
        {
            var threshold = DateTime.UtcNow - _heartbeatTimeout;
            foreach (var (adapterId, connection) in _connectionsByAdapterId.ToList())
            {
                if (connection.LastSeenAt >= threshold)
                {
                    continue;
                }

                _logger.LogWarning($"[TcpBridge] Adapter '{adapterId}' hat seit {_heartbeatTimeout} kein Lebenszeichen gesendet.");
                _ = connection.DisposeAsync();
            }
        }

        private static IPAddress ResolveBindAddress(string bindHost)
        {
            if (string.IsNullOrWhiteSpace(bindHost) || string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return IPAddress.Loopback;
            }

            if (string.Equals(bindHost, "*", StringComparison.Ordinal) || string.Equals(bindHost, "0.0.0.0", StringComparison.Ordinal))
            {
                return IPAddress.Any;
            }

            if (IPAddress.TryParse(bindHost, out var address))
            {
                return address;
            }

            return Dns.GetHostAddresses(bindHost).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new ArgumentException($"Bind-Adresse '{bindHost}' konnte nicht aufgelöst werden.", nameof(bindHost));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts?.Dispose();
        }
    }
}
