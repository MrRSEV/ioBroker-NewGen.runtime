using System;
using RSEV.Utilities.ApiBuilder;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Wird ausgelöst, wenn ein Adapter den für den Core reservierten
    /// RuntimeApiHost/WebHostService direkt verwenden möchte.
    /// </summary>
    public sealed class WebHostServiceAccessException : InvalidOperationException
    {
        public WebHostServiceAccessException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Veralteter Vertrag. Neue Adapter verwenden <see cref="ioBroker_NewGen.Core.Web.IWebEndpointRegistry"/>;
    /// ein direkter Zugriff auf RuntimeApiHost/WebHostService wird abgewiesen.
    /// </summary>
    public interface IWebHostService
    {
        bool IsRunning { get; }

        bool Register(IRuntimeEndpoint endpoint);
    }

    /// <summary>
    /// Veralteter Kompatibilitäts-Typ. Adapter dürfen den RuntimeApiHost nicht
    /// direkt verwenden; Web-Endpunkte laufen über IWebEndpointRegistry und den
    /// separat installierbaren Web-Adapter.
    /// </summary>
    public sealed class WebHostService : IWebHostService
    {
        private readonly RuntimeApiHost _host;
        private readonly ILogger? _logger;

        public WebHostService(RuntimeApiHost host, ILogger? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
        }

        public bool IsRunning => DenyAccess();

        public bool Register(IRuntimeEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            return DenyAccess();
        }

        private bool DenyAccess()
        {
            const string message = "Direkter RuntimeApiHost/WebHostService-Zugriff ist für Adapter verboten. IWebEndpointRegistry verwenden.";
            _logger?.LogError($"[RuntimeApiHost] {message}");
            throw new WebHostServiceAccessException(message);
        }
    }
}
