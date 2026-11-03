using System;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Bridge;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Routing
{
    /// <summary>
    /// Entscheidet anhand des Adaptertyps, ob ein <see cref="BusFrame"/>
    /// intern (direkt �ber den <see cref="SystemMessageBus"/>) oder extern
    /// (�ber die TCP-Bridge, Legacy-ioBroker-Adapter) transportiert wird.
    /// </summary>
    /// <remarks>
    /// Seit Sprint 2 ist die TCP-Bridge-Anbindung f�r <see cref="AdapterType.IoBroker"/>
    /// aktiv (<see cref="TcpBridgeServer"/>). Externe Payloads werden gem��
    /// Architektur 1:1 auf den Systembus gespiegelt.
    /// </remarks>
    public sealed class AdapterRouter
    {
        private readonly SystemMessageBus _systemBus;
        private readonly ILogger _logger;
        private TcpBridgeServer? _tcpBridge;

        public AdapterRouter(SystemMessageBus systemBus, ILogger logger)
        {
            _systemBus = systemBus ?? throw new ArgumentNullException(nameof(systemBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Verkn�pft den Router mit der TCP-Bridge, sobald diese gestartet wurde.
        /// Ohne verkn�pfte Bridge werden <see cref="AdapterType.IoBroker"/>-Frames
        /// nur geloggt (Fallback-Verhalten wie in Sprint 1).
        /// </summary>
        public void AttachTcpBridge(TcpBridgeServer tcpBridge)
        {
            _tcpBridge = tcpBridge ?? throw new ArgumentNullException(nameof(tcpBridge));
        }

        /// <summary>
        /// Routet ein Frame gem�� Adaptertyp:
        /// <list type="bullet">
        /// <item><see cref="AdapterType.Custom"/> ? direkt �ber den Systembus (intern).</item>
        /// <item><see cref="AdapterType.IoBroker"/> ? TCP-Bridge an den Ziel-Adapter.</item>
        /// </list>
        /// </summary>
        public void Route(BusFrame frame, AdapterType adapterType)
        {
            ArgumentNullException.ThrowIfNull(frame);

            switch (adapterType)
            {
                case AdapterType.Custom:
                    RouteInternal(frame);
                    break;

                case AdapterType.IoBroker:
                    RouteToTcpBridge(frame);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(adapterType), adapterType, "Unbekannter Adaptertyp.");
            }
        }

        /// <summary>
        /// Spiegelt ein von extern empfangenes Payload 1:1 auf den Systembus.
        /// </summary>
        public void RouteExternalPayload(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            RouteInternal(frame);
        }

        private void RouteInternal(BusFrame frame)
        {
            _systemBus.Publish(frame);
        }

        private void RouteToTcpBridge(BusFrame frame)
        {
            if (_tcpBridge is null)
            {
                _logger.LogWarning(
                    $"[AdapterRouter] Frame f�r Adresse '{frame.Address}' von Adapter '{frame.AdapterId}' " +
                    "sollte an die TCP-Bridge gehen, es ist aber keine Bridge verkn�pft.");
                return;
            }

            // Zieladapter ist �ber frame.AdapterId adressiert (Format "Name.id").
            _ = _tcpBridge.SendToAdapterAsync(frame.AdapterId, frame);
        }
    }
}
