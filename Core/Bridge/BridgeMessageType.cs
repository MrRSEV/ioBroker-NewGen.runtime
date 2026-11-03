namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Nachrichtentyp eines TCP-Bridge-Envelopes.
    /// </summary>
    public enum BridgeMessageType
    {
        /// <summary>
        /// Adapter ? Host: Erstkontakt, sendet den eigenen Namen.
        /// </summary>
        Handshake,

        /// <summary>
        /// Host ? Adapter: Best�tigt den Handshake und teilt die vergebene ID mit.
        /// </summary>
        HandshakeAck,

        /// <summary>
        /// Bidirektional: Transportiert ein <see cref="ioBroker_NewGen.Core.Bus.BusFrame"/>.
        /// </summary>
        Frame,

        /// <summary>
        /// Bidirektional: Lebenszeichen zur Verbindungs�berwachung.
        /// </summary>
        Heartbeat,

        /// <summary>
        /// Host ? Adapter oder Adapter ? Host: Fehlermeldung.
        /// </summary>
        Error
    }
}
