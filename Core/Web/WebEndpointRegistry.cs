using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Web
{
    /// <summary>
    /// Protokolltyp eines vom externen Web-Adapters verwalteten Endpoints.
    /// </summary>
    public enum WebEndpointKind
    {
        Http,
        WebSocket
    }

    /// <summary>
    /// Adapter-neutraler HTTP-/WebSocket-Request.
    /// </summary>
    public sealed class WebEndpointRequest
    {
        public required string Route { get; init; }

        public string Method { get; init; } = "GET";

        /// <summary>Query-String ohne führendes "?" (leer, wenn keiner vorhanden ist).</summary>
        public string Query { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public byte[] Body { get; init; } = Array.Empty<byte>();

        public bool IsWebSocket { get; init; }

        public IWebSocketSession? WebSocket { get; init; }
    }

    /// <summary>Adapter-neutrale HTTP-Antwort. Bei WebSockets wird die Session direkt verwendet.</summary>
    public sealed class WebEndpointResponse
    {
        public int StatusCode { get; init; } = 200;

        public IReadOnlyDictionary<string, string> Headers { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public byte[] Body { get; init; } = Array.Empty<byte>();

        public static WebEndpointResponse Text(int statusCode, string text, string contentType = "text/plain; charset=utf-8")
        {
            return new WebEndpointResponse
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = contentType
                },
                Body = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty)
            };
        }
    }

    public sealed record WebSocketMessage(byte[] Data, bool IsText, bool EndOfMessage);

    /// <summary>
    /// Kleine Abstraktion über den WebSocket des externen Web-Adapters. Sie hält
    /// Core- und Adapterverträge frei von HttpListener-/WebSocket-Details.
    /// </summary>
    public interface IWebSocketSession
    {
        bool IsOpen { get; }

        Task SendTextAsync(string text, CancellationToken cancellationToken = default);

        Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

        Task<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default);

        Task CloseAsync(CancellationToken cancellationToken = default);
    }

    public delegate Task<WebEndpointResponse> WebEndpointHandler(
        WebEndpointRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Zentraler, webserver-unabhängiger Registrierungs- und Dispatch-Vertrag.
    /// Die konkrete Listener-Implementierung liegt im externen Web-Adapter.
    /// </summary>
    public interface IWebEndpointRegistry
    {
        IReadOnlyCollection<WebEndpointRegistration> Registrations { get; }

        bool Register(string ownerId, string route, WebEndpointKind kind, WebEndpointHandler handler);

        bool Unregister(string ownerId, string route);

        bool CanDispatch(string route, WebEndpointKind kind);

        Task<WebEndpointResponse> DispatchAsync(WebEndpointRequest request, CancellationToken cancellationToken = default);
    }

    public sealed record WebEndpointRegistration(
        string OwnerId,
        string Route,
        WebEndpointKind Kind,
        WebEndpointHandler Handler);

    /// <summary>
    /// Thread-sichere Registry für native und über die Legacy-Bridge registrierte
    /// Web-Endpunkte. Sie öffnet selbst keinen Port.
    /// </summary>
    public sealed class WebEndpointRegistry : IWebEndpointRegistry
    {
        // Schlüssel = Art + Route, damit HTTP und WebSocket dieselbe Route (z. B. "/") teilen können.
        // Routen mit der Endung "/*" gelten als Präfix-Routen.
        private readonly ConcurrentDictionary<string, WebEndpointRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<WebEndpointRegistration> Registrations => _registrations.Values.ToArray();

        private static string KeyOf(WebEndpointKind kind, string route) => $"{kind}:{route}";

        public bool Register(string ownerId, string route, WebEndpointKind kind, WebEndpointHandler handler)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Owner-ID darf nicht leer sein.", nameof(ownerId));
            if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/')) throw new ArgumentException("Route muss mit '/' beginnen.", nameof(route));
            ArgumentNullException.ThrowIfNull(handler);

            var normalized = NormalizeRoute(route);
            var key = KeyOf(kind, normalized);
            var registration = new WebEndpointRegistration(ownerId, normalized, kind, handler);
            while (true)
            {
                if (_registrations.TryGetValue(key, out var current) && !string.Equals(current.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (current is null)
                {
                    if (_registrations.TryAdd(key, registration)) return true;
                    continue;
                }

                return _registrations.TryUpdate(key, registration, current);
            }
        }

        public bool Unregister(string ownerId, string route)
        {
            var normalized = NormalizeRoute(route);
            var removed = false;
            foreach (var kind in new[] { WebEndpointKind.Http, WebEndpointKind.WebSocket })
            {
                var key = KeyOf(kind, normalized);
                removed |= _registrations.TryGetValue(key, out var current) &&
                           string.Equals(current.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase) &&
                           _registrations.TryRemove(new KeyValuePair<string, WebEndpointRegistration>(key, current));
            }

            return removed;
        }

        private WebEndpointRegistration? Find(WebEndpointKind kind, string route)
        {
            if (_registrations.TryGetValue(KeyOf(kind, route), out var exact)) return exact;

            WebEndpointRegistration? best = null;
            foreach (var candidate in _registrations.Values)
            {
                if (candidate.Kind != kind || !candidate.Route.EndsWith("/*", StringComparison.Ordinal)) continue;
                var prefix = candidate.Route[..^1];
                if (!route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || candidate.Route.Length > best.Route.Length) best = candidate;
            }

            return best;
        }

        public bool CanDispatch(string route, WebEndpointKind kind)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            return Find(kind, NormalizeRoute(route)) is not null;
        }

        public async Task<WebEndpointResponse> DispatchAsync(WebEndpointRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var route = NormalizeRoute(request.Route);
            var kind = request.IsWebSocket ? WebEndpointKind.WebSocket : WebEndpointKind.Http;
            var registration = Find(kind, route);
            if (registration is null)
            {
                var other = Find(request.IsWebSocket ? WebEndpointKind.Http : WebEndpointKind.WebSocket, route);
                return other is null
                    ? WebEndpointResponse.Text(404, "Web endpoint not found.")
                    : WebEndpointResponse.Text(426, "WebSocket upgrade required.");
            }

            try
            {
                return await registration.Handler(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return WebEndpointResponse.Text(500, "Web endpoint failed.");
            }
        }

        private static string NormalizeRoute(string route)
        {
            var normalized = route.Trim();
            if (!normalized.StartsWith('/')) normalized = "/" + normalized;
            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }
    }
}
