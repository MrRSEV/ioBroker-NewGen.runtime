using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Events;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Web;
using RSEV.Utilities.ApiBuilder;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Kompatibilitätsdienste für anspruchsvolle Legacy-Adapter.
    /// Semantische Metadaten, Runtime-Kommandos und Dateizugriffe liegen hier
    /// außerhalb des Realtime-State-Schreibpfads.
    /// </summary>
    public sealed class NodeAdapterRuntimeApi : IDisposable
    {
        public const string Objective = "Provide compatibility services for advanced legacy adapters.";

        public static IReadOnlyList<string> Features { get; } = new[]
        {
            "sendTo Support",
            "State Subscription API",
            "Object Subscription API",
            "Notification API",
            "Metadata API",
            "File Storage API",
            "Web Endpoint Registration API",
            "Process Lifecycle API"
        };

        public static NodeAdapterWebIntegration WebIntegration { get; } = new();

        private readonly SemanticStateMatrix? _matrix;
        private readonly SystemMessageBus? _systemBus;
        private readonly AdapterRegistry _adapterRegistry;
        private readonly ILogger _logger;
        private readonly IWebEndpointRegistry? _webEndpointRegistry;
        private readonly string _storageRoot;
        private readonly object _subscriptionLock = new();
        private readonly List<AdapterSubscription> _subscriptions = new();
        private readonly ConcurrentDictionary<string, JsonNode?> _objects = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeWebResponse>> _webRequests = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, WebSocketRelay> _webSocketRelays = new(StringComparer.Ordinal);
        private Func<string, BridgeEnvelope, CancellationToken, Task<bool>>? _sendEnvelope;
        private NodeAdapterRuntime? _runtime;
        private bool _disposed;

        public NodeAdapterRuntimeApi(
            SemanticStateMatrix? matrix,
            SystemMessageBus? systemBus,
            AdapterRegistry adapterRegistry,
            ILogger logger,
            IWebHostService? webHostService = null,
            string? storageRoot = null,
            IWebEndpointRegistry? webEndpointRegistry = null)
        {
            _matrix = matrix;
            _systemBus = systemBus;
            _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // webHostService bleibt nur als Binär-/Quellkompatibilitätsparameter
            // erhalten. Neue Registrierungen gehen ausschließlich über die
            // webserver-unabhängige Registry.
            _webEndpointRegistry = webEndpointRegistry;
            _storageRoot = Path.GetFullPath(storageRoot ?? Path.Combine(AppContext.BaseDirectory, "data", "adapter-storage"));

            if (_matrix is not null)
            {
                _matrix.StateChanged += OnStateChanged;
                _matrix.ObjectChanged += OnObjectChanged;
            }
        }

        public NodeAdapterRuntimeApiContract Contract => new(Objective, Features, WebIntegration);

        public event EventHandler<NodeAdapterNotificationEventArgs>? NotificationReceived;

        /// <summary>Verbindet die API mit der TCP-Bridge, ohne die API an deren Listener zu koppeln.</summary>
        internal void AttachTransport(Func<string, BridgeEnvelope, CancellationToken, Task<bool>> sendEnvelope)
        {
            _sendEnvelope = sendEnvelope ?? throw new ArgumentNullException(nameof(sendEnvelope));
        }

        internal void AttachRuntime(NodeAdapterRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>Sendet eine ioBroker-kompatible Message an einen Zieladapter.</summary>
        public Task<bool> SendToAsync(
            string targetAdapterId,
            string command,
            object? message = null,
            string? fromAdapterId = null,
            string frame = "Message",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetAdapterId)) throw new ArgumentException("Zieladapter darf nicht leer sein.", nameof(targetAdapterId));
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command darf nicht leer sein.", nameof(command));

            var metadata = new Dictionary<string, string>
            {
                ["frame"] = frame,
                ["command"] = command
            };
            if (!string.IsNullOrWhiteSpace(fromAdapterId)) metadata["from"] = fromAdapterId;

            return SendPayloadAsync(
                NormalizeAdapterId(targetAdapterId),
                targetAdapterId,
                message,
                BusFrameKind.Command,
                metadata,
                cancellationToken);
        }

        public Task<bool> SendToHostAsync(
            string host,
            string command,
            object? message = null,
            string? fromAdapterId = null,
            CancellationToken cancellationToken = default)
            => SendToAsync(host, command, message, fromAdapterId, "HostMessage", cancellationToken);

        /// <summary>Registriert eine State-Subscription für einen Adapter.</summary>
        public bool SubscribeStates(string adapterId, string pattern)
            => AddSubscription(adapterId, pattern, SubscriptionTarget.State);

        public bool UnsubscribeStates(string adapterId, string pattern)
            => RemoveSubscription(adapterId, pattern, SubscriptionTarget.State);

        public bool SubscribeState(string adapterId, string pattern) => SubscribeStates(adapterId, pattern);

        public bool UnsubscribeState(string adapterId, string pattern) => UnsubscribeStates(adapterId, pattern);

        /// <summary>Registriert eine Objekt-Subscription für einen Adapter.</summary>
        public bool SubscribeObjects(string adapterId, string pattern)
            => AddSubscription(adapterId, pattern, SubscriptionTarget.Object);

        public bool UnsubscribeObjects(string adapterId, string pattern)
            => RemoveSubscription(adapterId, pattern, SubscriptionTarget.Object);

        public bool SubscribeObject(string adapterId, string pattern) => SubscribeObjects(adapterId, pattern);

        public bool UnsubscribeObject(string adapterId, string pattern) => UnsubscribeObjects(adapterId, pattern);

        /// <summary>Veröffentlicht eine Host-Notification und löst lokale Listener aus.</summary>
        public void Notify(string adapterId, string level, string message, object? payload = null)
        {
            var args = new NodeAdapterNotificationEventArgs(adapterId, level, message, payload);
            try
            {
                NotificationReceived?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[NodeRuntimeApi] Notification-Listener fehlgeschlagen: {ex.Message}");
            }
        }

        public void PublishNotification(string adapterId, string level, string message, object? payload = null)
            => Notify(adapterId, level, message, payload);

        public IReadOnlyDictionary<string, string> GetMetadata(string adapterId)
        {
            var registration = _adapterRegistry.Get(adapterId);
            if (registration is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var metadata = new Dictionary<string, string>(registration.Metadata, StringComparer.Ordinal)
            {
                ["id"] = registration.Id,
                ["name"] = registration.Name,
                ["type"] = registration.Type.ToString(),
                ["status"] = registration.Status.ToString()
            };
            return metadata;
        }

        public bool SetMetadata(string adapterId, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || _adapterRegistry.Get(adapterId) is not { } registration)
            {
                return false;
            }

            registration.Metadata[key] = value ?? string.Empty;
            return true;
        }

        public async Task<byte[]?> ReadFileAsync(string adapterId, string relativePath, CancellationToken cancellationToken = default)
        {
            var path = ResolveStoragePath(adapterId, relativePath);
            if (!File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }

        public async Task WriteFileAsync(string adapterId, string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            var path = ResolveStoragePath(adapterId, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
        }

        public bool DeleteFile(string adapterId, string relativePath)
        {
            var path = ResolveStoragePath(adapterId, relativePath);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public IReadOnlyList<string> ListFiles(string adapterId)
        {
            var directory = ResolveStoragePath(adapterId, ".");
            if (!Directory.Exists(directory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Veralteter Zugriff auf RuntimeApiHost/WebHostService. Dieser Pfad ist
        /// absichtlich gesperrt, damit Adapter den zentralen Web-Adapter nutzen.
        /// </summary>
        [Obsolete("RuntimeApiHost/WebHostService ist für Adapter nicht verfügbar. IWebEndpointRegistry verwenden.")]
        public bool RegisterWebEndpoint(IRuntimeEndpoint endpoint)
        {
            throw new WebHostServiceAccessException("Direkter RuntimeApiHost/WebHostService-Zugriff ist für Adapter verboten.");
        }

        /// <summary>
        /// Registriert einen Endpoint für einen Legacy-Adapter. HTTP-Aufrufe
        /// werden als korrelierte Bridge-Requests an den Adapter delegiert.
        /// </summary>
        public bool RegisterWebEndpoint(string adapterId, string route)
            => RegisterWebEndpoint(adapterId, route, WebEndpointKind.Http);

        private bool RegisterWebEndpoint(string adapterId, string route, WebEndpointKind kind)
        {
            if (string.IsNullOrWhiteSpace(adapterId) || string.IsNullOrWhiteSpace(route) || _webEndpointRegistry is null)
            {
                return false;
            }

            return _webEndpointRegistry.Register(
                adapterId,
                route,
                kind,
                (request, cancellationToken) => ForwardWebRequestAsync(adapterId, request, cancellationToken));
        }

        public bool WebHostIsRunning => false;

        public Task<AdapterRegistration> StartAdapterAsync(string name, string scriptPath, string[]? arguments = null, string? instanceId = null, string? adapterDirectory = null)
            => RuntimeRequired.StartAdapterAsync(name, scriptPath, arguments, instanceId, adapterDirectory);

        public Task<bool> StopAdapterAsync(string adapterId) => RuntimeRequired.StopAdapterAsync(adapterId);

        public Task<bool> RestartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
            => RuntimeRequired.RestartAdapterAsync(adapterId, cancellationToken);

        public bool IsAdapterRunning(string adapterId) => _runtime?.IsRunning(adapterId) == true;

        public Task StopAllAdaptersAsync() => RuntimeRequired.StopAllAsync();

        /// <summary>
        /// Verarbeitet die von AdapterCore verwendeten Command-Frames. Gibt
        /// <c>false</c> zurück, wenn der Frame für den allgemeinen Systembus
        /// bestimmt ist.
        /// </summary>
        internal async Task<bool> HandleCommandAsync(string sourceAdapterId, BridgeEnvelope envelope, CancellationToken cancellationToken)
        {
            var metadata = envelope.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var frame = metadata.TryGetValue("frame", out var frameName) ? frameName : string.Empty;
            if (string.IsNullOrWhiteSpace(frame)) return false;

            if (metadata.TryGetValue("reply", out var reply) &&
                string.Equals(reply, "true", StringComparison.OrdinalIgnoreCase) &&
                metadata.TryGetValue("correlationId", out var responseCorrelation) &&
                _webRequests.TryRemove(responseCorrelation, out var webRequest))
            {
                webRequest.TrySetResult(BridgeWebResponse.From(DecodePayload(envelope)));
                return true;
            }

            switch (frame.ToLowerInvariant())
            {
                case "statequery":
                    await ReplyAsync(sourceAdapterId, envelope, frame, QueryStates(envelope.Address, metadata), cancellationToken);
                    return true;
                case "objectquery":
                    await ReplyAsync(sourceAdapterId, envelope, frame, QueryObject(envelope.Address), cancellationToken);
                    return true;
                case "objectcreate":
                    await StoreObjectAsync(sourceAdapterId, envelope.Address, DecodePayload(envelope), false, envelope, cancellationToken);
                    return true;
                case "objectextend":
                    await StoreObjectAsync(sourceAdapterId, envelope.Address, DecodePayload(envelope), true, envelope, cancellationToken);
                    return true;
                case "objectdelete":
                    await DeleteObjectAsync(sourceAdapterId, envelope, metadata, cancellationToken);
                    return true;
                case "subscribe":
                    AddSubscription(sourceAdapterId, envelope.Address ?? "*", ParseTarget(metadata));
                    return true;
                case "unsubscribe":
                    RemoveSubscription(sourceAdapterId, envelope.Address ?? "*", ParseTarget(metadata));
                    return true;
                case "message":
                case "hostmessage":
                    await HandleMessageAsync(sourceAdapterId, envelope, frame, metadata, cancellationToken);
                    return true;
                case "notification":
                    Notify(sourceAdapterId, ReadMetadata(metadata, "level", "info"), ReadMetadata(metadata, "message", DecodePayload(envelope)?.ToString() ?? string.Empty), DecodePayload(envelope));
                    await ReplyAsync(sourceAdapterId, envelope, frame, new { accepted = true }, cancellationToken);
                    return true;
                case "metadataquery":
                case "metadata":
                    await HandleMetadataAsync(sourceAdapterId, envelope, metadata, cancellationToken);
                    return true;
                case "filestorage":
                    try
                    {
                        await HandleFileStorageAsync(sourceAdapterId, envelope, metadata, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[NodeRuntimeApi] File-Storage-Anfrage abgewiesen: {ex.Message}");
                        await ReplyAsync(sourceAdapterId, envelope, frame, new { success = false, error = "file access denied" }, cancellationToken);
                    }
                    return true;
                case "webendpoint":
                    await HandleWebEndpointAsync(sourceAdapterId, envelope, metadata, cancellationToken);
                    return true;
                case "processlifecycle":
                    await HandleProcessLifecycleAsync(sourceAdapterId, envelope, metadata, cancellationToken);
                    return true;
                case "websocketmessage":
                    await HandleWebSocketMessageAsync(envelope, cancellationToken);
                    return true;
                case "websocketclose":
                    await HandleWebSocketCloseAsync(envelope, cancellationToken);
                    return true;
                default:
                    return false;
            }
        }

        private object QueryStates(string? address, IReadOnlyDictionary<string, string> metadata)
        {
            if (_matrix is null) return metadata.ContainsKey("multi") ? new Dictionary<string, object?>() : null!;
            var pattern = string.IsNullOrWhiteSpace(address) ? "*" : address;
            if (metadata.ContainsKey("multi"))
            {
                return _matrix.GetStates(pattern).ToDictionary(entry => entry.Key, entry => (object?)ToStatePayload(entry.Value), StringComparer.Ordinal);
            }

            return _matrix.GetState(pattern) is { } state ? ToStatePayload(state) : null!;
        }

        private object? QueryObject(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            if (_objects.TryGetValue(address, out var document)) return document;
            var matrixObject = _matrix?.GetObject(address);
            return matrixObject is null ? null : new
            {
                _id = matrixObject.Id,
                type = matrixObject.Type,
                common = new { name = matrixObject.Name },
                meta = matrixObject.Meta
            };
        }

        private async Task StoreObjectAsync(string sourceAdapterId, string? address, object? payload, bool extend, BridgeEnvelope request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            var node = ToJsonNode(payload) as JsonObject ?? new JsonObject();
            JsonNode? stored;
            if (extend && _objects.TryGetValue(address, out var existing) && existing is JsonObject existingObject)
            {
                Merge(existingObject, node);
                stored = existingObject;
            }
            else
            {
                stored = node;
                _objects[address] = stored;
            }

            var matrixObject = ToMatrixObject(address, stored as JsonObject);
            if (_matrix is not null)
            {
                if (!_matrix.UpdateObject(matrixObject) && !_matrix.RegisterObject(matrixObject))
                {
                    _logger.LogWarning($"[NodeRuntimeApi] Objekt '{address}' konnte nicht gespeichert werden.");
                }
            }

            if (_matrix is null)
            {
                PublishObjectChange(address, stored);
            }
            await ReplyAsync(sourceAdapterId, request, extend ? "ObjectExtend" : "ObjectCreate", new { id = address }, cancellationToken);
        }

        private async Task DeleteObjectAsync(string sourceAdapterId, BridgeEnvelope request, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var address = request.Address;
            if (string.IsNullOrWhiteSpace(address)) return;
            _objects.TryRemove(address, out _);
            _matrix?.RemoveObject(address);
            if (_matrix is null)
            {
                PublishObjectChange(address, null);
            }
            await ReplyAsync(sourceAdapterId, request, "ObjectDelete", new { id = address }, cancellationToken);
        }

        private async Task HandleMessageAsync(string sourceAdapterId, BridgeEnvelope request, string frame, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var target = NormalizeAdapterId(request.Address);
            var outgoing = Clone(request);
            outgoing.AdapterId = sourceAdapterId;
            outgoing.Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
            outgoing.Metadata["frame"] = frame;
            var sent = !string.IsNullOrWhiteSpace(target) && await SendToAdapterEnvelopeAsync(target, outgoing, cancellationToken);

            if (!sent && string.Equals(frame, "HostMessage", StringComparison.OrdinalIgnoreCase))
            {
                Notify(sourceAdapterId, "info", $"HostMessage '{metadata.GetValueOrDefault("command", string.Empty)}'", DecodePayload(request));
            }

            if (!sent && metadata.ContainsKey("expectReply"))
            {
                await ReplyAsync(sourceAdapterId, request, frame, new { sent = false }, cancellationToken);
            }
        }

        private async Task HandleMetadataAsync(string sourceAdapterId, BridgeEnvelope request, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var operation = ReadMetadata(metadata, "operation", "get");
            var key = request.Address ?? string.Empty;
            if (string.Equals(operation, "set", StringComparison.OrdinalIgnoreCase))
            {
                var value = DecodePayload(request)?.ToString() ?? string.Empty;
                SetMetadata(sourceAdapterId, key, value);
            }

            object? result = string.Equals(operation, "get", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(key)
                ? GetMetadata(sourceAdapterId).GetValueOrDefault(key)
                : GetMetadata(sourceAdapterId);
            await ReplyAsync(sourceAdapterId, request, "Metadata", result, cancellationToken);
        }

        private async Task HandleFileStorageAsync(string sourceAdapterId, BridgeEnvelope request, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var operation = ReadMetadata(metadata, "operation", "read").ToLowerInvariant();
            var relativePath = request.Address ?? string.Empty;
            switch (operation)
            {
                case "read":
                    var content = await ReadFileAsync(sourceAdapterId, relativePath, cancellationToken);
                    await ReplyAsync(sourceAdapterId, request, "FileStorage", content is null ? null : new { path = relativePath, contentBase64 = Convert.ToBase64String(content) }, cancellationToken);
                    break;
                case "write":
                    var payload = DecodePayload(request) as JsonElement?;
                    var base64 = payload is { ValueKind: JsonValueKind.Object } && payload.Value.TryGetProperty("contentBase64", out var contentProperty)
                        ? contentProperty.GetString()
                        : null;
                    if (base64 is not null) await WriteFileAsync(sourceAdapterId, relativePath, Convert.FromBase64String(base64), cancellationToken);
                    await ReplyAsync(sourceAdapterId, request, "FileStorage", new { written = base64 is not null }, cancellationToken);
                    break;
                case "delete":
                    await ReplyAsync(sourceAdapterId, request, "FileStorage", new { deleted = DeleteFile(sourceAdapterId, relativePath) }, cancellationToken);
                    break;
                case "list":
                    await ReplyAsync(sourceAdapterId, request, "FileStorage", ListFiles(sourceAdapterId), cancellationToken);
                    break;
            }
        }

        private async Task HandleWebEndpointAsync(string sourceAdapterId, BridgeEnvelope request, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var operation = ReadMetadata(metadata, "operation", "register");
            var payload = DecodePayload(request) as JsonElement?;
            var route = request.Address;
            if (payload is { ValueKind: JsonValueKind.Object } && payload.Value.TryGetProperty("route", out var routeProperty))
            {
                route = routeProperty.GetString() ?? route;
            }

            var kind = string.Equals(ReadMetadata(metadata, "kind", "http"), "websocket", StringComparison.OrdinalIgnoreCase)
                ? WebEndpointKind.WebSocket
                : WebEndpointKind.Http;
            var registered = string.Equals(operation, "register", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(route) &&
                             RegisterWebEndpoint(sourceAdapterId, route, kind);
            await ReplyAsync(sourceAdapterId, request, "WebEndpoint", new { registered, route }, cancellationToken);
        }

        private async Task HandleWebSocketMessageAsync(BridgeEnvelope envelope, CancellationToken cancellationToken)
        {
            if (envelope.Metadata is null ||
                !envelope.Metadata.TryGetValue("correlationId", out var correlationId) ||
                !_webSocketRelays.TryGetValue(correlationId, out var relay))
            {
                return;
            }

            if (DecodePayload(envelope) is not JsonElement payload ||
                !payload.TryGetProperty("dataBase64", out var dataProperty) ||
                dataProperty.GetString() is not { } dataBase64)
            {
                return;
            }

            var data = Convert.FromBase64String(dataBase64);
            var isText = payload.TryGetProperty("isText", out var textProperty) && textProperty.GetBoolean();
            if (isText)
            {
                await relay.Session.SendTextAsync(Encoding.UTF8.GetString(data), cancellationToken);
            }
            else
            {
                await relay.Session.SendBinaryAsync(data, cancellationToken);
            }
        }

        private async Task HandleWebSocketCloseAsync(BridgeEnvelope envelope, CancellationToken cancellationToken)
        {
            if (envelope.Metadata is null ||
                !envelope.Metadata.TryGetValue("correlationId", out var correlationId) ||
                !_webSocketRelays.TryRemove(correlationId, out var relay))
            {
                return;
            }

            relay.Cancellation.Cancel();
            await relay.Session.CloseAsync(cancellationToken);
        }

        private async Task HandleProcessLifecycleAsync(string sourceAdapterId, BridgeEnvelope request, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var operation = ReadMetadata(metadata, "operation", "status").ToLowerInvariant();
            var result = operation switch
            {
                "status" => new { success = true, running = IsAdapterRunning(sourceAdapterId) },
                "restart" => new { success = await RestartAdapterAsync(sourceAdapterId, cancellationToken), running = IsAdapterRunning(sourceAdapterId) },
                "stop" => new { success = await StopAdapterAsync(sourceAdapterId), running = IsAdapterRunning(sourceAdapterId) },
                _ => new { success = false, running = IsAdapterRunning(sourceAdapterId) }
            };
            await ReplyAsync(sourceAdapterId, request, "ProcessLifecycle", result, cancellationToken);
        }

        private async Task ReplyAsync(string adapterId, BridgeEnvelope request, string frame, object? payload, CancellationToken cancellationToken)
        {
            if (request.Metadata is null || !request.Metadata.ContainsKey("expectReply")) return;
            if (!request.Metadata.TryGetValue("correlationId", out var correlationId)) return;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["frame"] = frame,
                ["reply"] = "true",
                ["correlationId"] = correlationId
            };
            await SendPayloadAsync(adapterId, request.Address, payload, BusFrameKind.Command, metadata, cancellationToken);
        }

        private async Task<bool> SendPayloadAsync(string targetAdapterId, string? address, object? payload, BusFrameKind kind, IDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            var envelope = new BridgeEnvelope
            {
                Type = BridgeMessageType.Frame,
                AdapterId = targetAdapterId,
                Address = address,
                ValueType = "json",
                FrameKind = (int)kind,
                PayloadBase64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)),
                Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                Timestamp = DateTime.UtcNow
            };
            return await SendToAdapterEnvelopeAsync(targetAdapterId, envelope, cancellationToken);
        }

        private async Task<bool> SendToAdapterEnvelopeAsync(string targetAdapterId, BridgeEnvelope envelope, CancellationToken cancellationToken)
            => _sendEnvelope is not null && await _sendEnvelope(targetAdapterId, envelope, cancellationToken);

        private void OnStateChanged(StateChangedEventArgs changed)
        {
            AdapterSubscription[] subscriptions;
            lock (_subscriptionLock)
            {
                subscriptions = _subscriptions.Where(subscription => subscription.Target == SubscriptionTarget.State && Matches(subscription.Pattern, changed.StateId)).ToArray();
            }

            foreach (var subscription in subscriptions)
            {
                _ = SendPayloadAsync(
                    subscription.AdapterId,
                    changed.StateId,
                    ToStatePayload(changed.State),
                    BusFrameKind.StateUpdate,
                    new Dictionary<string, string> { ["frame"] = "StateChange" },
                    CancellationToken.None);
            }
        }

        private void PublishObjectChange(string id, JsonNode? value)
        {
            AdapterSubscription[] subscriptions;
            lock (_subscriptionLock)
            {
                subscriptions = _subscriptions.Where(subscription => subscription.Target == SubscriptionTarget.Object && Matches(subscription.Pattern, id)).ToArray();
            }

            foreach (var subscription in subscriptions)
            {
                _ = SendPayloadAsync(
                    subscription.AdapterId,
                    id,
                    value,
                    BusFrameKind.Event,
                    new Dictionary<string, string> { ["frame"] = "ObjectChange" },
                    CancellationToken.None);
            }
        }

        private void OnObjectChanged(MatrixObjectChangedEventArgs changed)
        {
            var value = changed.Object is null
                ? null
                : _objects.TryGetValue(changed.ObjectId, out var document)
                    ? document
                    : ToJsonNode(new
            {
                _id = changed.Object.Id,
                type = changed.Object.Type,
                common = new { name = changed.Object.Name },
                meta = changed.Object.Meta
            });
            PublishObjectChange(changed.ObjectId, value);
        }

        private bool AddSubscription(string adapterId, string pattern, SubscriptionTarget target)
        {
            if (string.IsNullOrWhiteSpace(adapterId) || string.IsNullOrWhiteSpace(pattern)) return false;
            lock (_subscriptionLock)
            {
                if (_subscriptions.Any(subscription => subscription.AdapterId == adapterId && subscription.Pattern == pattern && subscription.Target == target)) return false;
                _subscriptions.Add(new AdapterSubscription(adapterId, pattern, target));
                return true;
            }
        }

        private bool RemoveSubscription(string adapterId, string pattern, SubscriptionTarget target)
        {
            lock (_subscriptionLock)
            {
                var removed = _subscriptions.RemoveAll(subscription => subscription.AdapterId == adapterId && subscription.Pattern == pattern && subscription.Target == target);
                return removed > 0;
            }
        }

        private string ResolveStoragePath(string adapterId, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("Adapter-ID darf nicht leer sein.", nameof(adapterId));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Dateipfad darf nicht leer sein.", nameof(relativePath));
            var adapterRoot = Path.GetFullPath(Path.Combine(_storageRoot, SanitizeSegment(adapterId)));
            var fullPath = Path.GetFullPath(Path.Combine(adapterRoot, relativePath));
            if (!fullPath.Equals(adapterRoot, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(adapterRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Dateipfad liegt außerhalb des Adapter-Speichers.");
            }

            return fullPath;
        }

        private static string SanitizeSegment(string value)
        {
            var segment = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
            {
                throw new InvalidOperationException("Ungültige Adapter-ID für den Dateispeicher.");
            }

            return segment;
        }

        private NodeAdapterRuntime RuntimeRequired => _runtime ?? throw new InvalidOperationException("NodeAdapterRuntime ist noch nicht initialisiert.");

        private static SubscriptionTarget ParseTarget(IReadOnlyDictionary<string, string> metadata)
            => string.Equals(ReadMetadata(metadata, "target", "state"), "object", StringComparison.OrdinalIgnoreCase) ? SubscriptionTarget.Object : SubscriptionTarget.State;

        private static string ReadMetadata(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
            => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        private static bool Matches(string pattern, string value)
            => Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string NormalizeAdapterId(string? adapterId)
        {
            if (string.IsNullOrWhiteSpace(adapterId)) return string.Empty;
            const string prefix = "system.adapter.";
            return adapterId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? adapterId[prefix.Length..] : adapterId;
        }

        private static object ToStatePayload(StateNode state) => new Dictionary<string, object?>
        {
            ["val"] = state.Value,
            ["ts"] = state.Timestamp,
            ["lc"] = state.Timestamp,
            ["ack"] = true,
            ["from"] = state.LastWrittenBy
        };

        private static object? DecodePayload(BridgeEnvelope envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.PayloadBase64)) return null;
            var bytes = Convert.FromBase64String(envelope.PayloadBase64);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }

        private static JsonNode? ToJsonNode(object? value)
        {
            if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
            return value is null ? null : JsonSerializer.SerializeToNode(value);
        }

        private static MatrixObject ToMatrixObject(string id, JsonObject? objectDocument)
        {
            var type = ReadJsonString(objectDocument, "type") ?? "state";
            var common = objectDocument?["common"] as JsonObject;
            var name = ReadJsonString(common, "name") ?? ReadJsonString(objectDocument, "name") ?? id;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in (common ?? new JsonObject()))
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text)) meta[property.Key] = text;
            }

            return new MatrixObject { Id = id, Name = name, Type = type, Meta = meta };
        }

        private static string? ReadJsonString(JsonObject? objectNode, string name)
        {
            if (objectNode is null || !objectNode.TryGetPropertyValue(name, out var node) || node is not JsonValue value) return null;
            return value.TryGetValue<string>(out var result) ? result : null;
        }

        private static void Merge(JsonObject target, JsonObject source)
        {
            foreach (var property in source)
            {
                if (property.Value is JsonObject sourceObject && target[property.Key] is JsonObject targetObject)
                {
                    Merge(targetObject, sourceObject);
                }
                else
                {
                    target[property.Key] = property.Value?.DeepClone();
                }
            }
        }

        private static BridgeEnvelope Clone(BridgeEnvelope source) => new()
        {
            Type = source.Type,
            AdapterId = source.AdapterId,
            Address = source.Address,
            ValueType = source.ValueType,
            FrameKind = source.FrameKind,
            PayloadBase64 = source.PayloadBase64,
            Metadata = source.Metadata is null ? null : new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal),
            Timestamp = source.Timestamp
        };

        private async Task<WebEndpointResponse> ForwardWebRequestAsync(string adapterId, WebEndpointRequest request, CancellationToken cancellationToken)
        {
            if (request.IsWebSocket)
            {
                return await ForwardLegacyWebSocketAsync(adapterId, request, cancellationToken);
            }

            var correlationId = $"web:{Guid.NewGuid():N}";
            var pending = new TaskCompletionSource<BridgeWebResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webRequests[correlationId] = pending;
            var sent = await SendPayloadAsync(
                adapterId,
                request.Route,
                new
                {
                    route = request.Route,
                    method = request.Method,
                    headers = request.Headers,
                    bodyBase64 = Convert.ToBase64String(request.Body)
                },
                BusFrameKind.Command,
                new Dictionary<string, string>
                {
                    ["frame"] = "WebEndpointRequest",
                    ["correlationId"] = correlationId,
                    ["expectReply"] = "true"
                },
                cancellationToken);
            if (!sent)
            {
                _webRequests.TryRemove(correlationId, out _);
                return new WebEndpointResponse
                {
                    StatusCode = 503,
                    Headers = new Dictionary<string, string>(),
                    Body = Array.Empty<byte>()
                };
            }

            try
            {
                var response = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                return new WebEndpointResponse
                {
                    StatusCode = response.StatusCode,
                    Headers = response.Headers,
                    Body = response.Body
                };
            }
            catch (TimeoutException)
            {
                _webRequests.TryRemove(correlationId, out _);
                return new WebEndpointResponse
                {
                    StatusCode = 504,
                    Headers = new Dictionary<string, string>(),
                    Body = Array.Empty<byte>()
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _webRequests.TryRemove(correlationId, out _);
                throw;
            }
        }

        private async Task<WebEndpointResponse> ForwardLegacyWebSocketAsync(string adapterId, WebEndpointRequest request, CancellationToken cancellationToken)
        {
            if (request.WebSocket is null)
            {
                return WebEndpointResponse.Text(400, "WebSocket session missing.");
            }

            var correlationId = $"ws:{Guid.NewGuid():N}";
            var pending = new TaskCompletionSource<BridgeWebResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var relay = new WebSocketRelay(adapterId, request.WebSocket, new CancellationTokenSource());
            _webRequests[correlationId] = pending;
            _webSocketRelays[correlationId] = relay;

            var sent = await SendPayloadAsync(
                adapterId,
                request.Route,
                new { route = request.Route, method = request.Method, headers = request.Headers },
                BusFrameKind.Command,
                new Dictionary<string, string>
                {
                    ["frame"] = "WebSocketOpen",
                    ["correlationId"] = correlationId,
                    ["expectReply"] = "true"
                },
                cancellationToken);
            if (!sent)
            {
                _webRequests.TryRemove(correlationId, out _);
                _webSocketRelays.TryRemove(correlationId, out _);
                relay.Cancellation.Dispose();
                return WebEndpointResponse.Text(503, "Legacy adapter unavailable.");
            }

            try
            {
                var openResponse = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (openResponse.StatusCode >= 400)
                {
                    return new WebEndpointResponse
                    {
                        StatusCode = openResponse.StatusCode,
                        Headers = openResponse.Headers,
                        Body = openResponse.Body
                    };
                }

                await RelayWebSocketToLegacyAsync(correlationId, relay, cancellationToken);
                return new WebEndpointResponse { StatusCode = 101 };
            }
            catch (TimeoutException)
            {
                return WebEndpointResponse.Text(504, "Legacy WebSocket handshake timed out.");
            }
            finally
            {
                _webRequests.TryRemove(correlationId, out _);
                _webSocketRelays.TryRemove(correlationId, out _);
                relay.Cancellation.Cancel();
                try
                {
                    await SendPayloadAsync(
                        adapterId,
                        request.Route,
                        null,
                        BusFrameKind.Command,
                        new Dictionary<string, string>
                        {
                            ["frame"] = "WebSocketClose",
                            ["correlationId"] = correlationId
                        },
                        CancellationToken.None);
                }
                catch
                {
                }

                relay.Cancellation.Dispose();
            }
        }

        private async Task RelayWebSocketToLegacyAsync(string correlationId, WebSocketRelay relay, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !relay.Cancellation.IsCancellationRequested)
            {
                var message = await relay.Session.ReceiveAsync(cancellationToken);
                if (message is null) break;
                await SendPayloadAsync(
                    relay.AdapterId,
                    string.Empty,
                    new
                    {
                        dataBase64 = Convert.ToBase64String(message.Data),
                        isText = message.IsText,
                        endOfMessage = message.EndOfMessage
                    },
                    BusFrameKind.Command,
                    new Dictionary<string, string>
                    {
                        ["frame"] = "WebSocketMessage",
                        ["correlationId"] = correlationId
                    },
                    cancellationToken);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_matrix is not null) _matrix.StateChanged -= OnStateChanged;
            if (_matrix is not null) _matrix.ObjectChanged -= OnObjectChanged;
            lock (_subscriptionLock) _subscriptions.Clear();
            foreach (var pending in _webRequests.Values) pending.TrySetCanceled();
            _webRequests.Clear();
            foreach (var relay in _webSocketRelays.Values) relay.Cancellation.Cancel();
            _webSocketRelays.Clear();
        }

        private sealed record AdapterSubscription(string AdapterId, string Pattern, SubscriptionTarget Target);

        private enum SubscriptionTarget
        {
            State,
            Object
        }

        private sealed class WebSocketRelay
        {
            public WebSocketRelay(string adapterId, IWebSocketSession session, CancellationTokenSource cancellation)
            {
                AdapterId = adapterId;
                Session = session;
                Cancellation = cancellation;
            }

            public string AdapterId { get; }
            public IWebSocketSession Session { get; }
            public CancellationTokenSource Cancellation { get; }
        }

        private sealed record BridgeWebResponse(int StatusCode, Dictionary<string, string> Headers, byte[] Body)
        {
            public static BridgeWebResponse From(object? payload)
            {
                if (payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
                {
                    return new BridgeWebResponse(500, new Dictionary<string, string>(), Array.Empty<byte>());
                }

                var statusCode = element.TryGetProperty("statusCode", out var status) && status.TryGetInt32(out var code) ? code : 200;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (element.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var header in headerElement.EnumerateObject()) headers[header.Name] = header.Value.GetString() ?? string.Empty;
                }

                var body = element.TryGetProperty("bodyBase64", out var bodyElement) && bodyElement.GetString() is { } bodyBase64
                    ? Convert.FromBase64String(bodyBase64)
                    : Array.Empty<byte>();
                return new BridgeWebResponse(statusCode, headers, body);
            }
        }
    }

    public sealed record NodeAdapterRuntimeApiContract(
        [property: JsonPropertyName("objective")] string Objective,
        [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
        [property: JsonPropertyName("webIntegration")] NodeAdapterWebIntegration WebIntegration);

    public sealed record NodeAdapterWebIntegration
    {
        [JsonPropertyName("delegateTo")]
        public string DelegateTo { get; init; } = "WebEndpointRegistry";

        [JsonPropertyName("adapterCreatesOwnWebServer")]
        public bool AdapterCreatesOwnWebServer { get; init; }
    }

    public sealed class NodeAdapterNotificationEventArgs : EventArgs
    {
        public NodeAdapterNotificationEventArgs(string adapterId, string level, string message, object? payload)
        {
            AdapterId = adapterId;
            Level = level;
            Message = message;
            Payload = payload;
        }

        public string AdapterId { get; }
        public string Level { get; }
        public string Message { get; }
        public object? Payload { get; }
    }
}
