namespace ioBroker_NewGen.Core.Registry
{
    /// <summary>
    /// Lebenszyklus-Status eines registrierten Adapters (Custom oder ioBroker-Legacy).
    /// </summary>
    public enum AdapterStatus
    {
        /// <summary>
        /// Adapter wurde registriert, Verbindung/Prozess aber noch nicht aufgebaut.
        /// </summary>
        Created,

        /// <summary>
        /// Verbindung/Prozess wird gerade aufgebaut (z. B. Node.js-Prozessstart, TCP-Handshake).
        /// </summary>
        Connecting,

        /// <summary>
        /// Adapter ist aktiv und betriebsbereit.
        /// </summary>
        Connected,

        /// <summary>
        /// Adapter wurde geordnet getrennt/gestoppt.
        /// </summary>
        Disconnected,

        /// <summary>
        /// Adapter ist abgest�rzt oder in einen Fehlerzustand geraten.
        /// </summary>
        Faulted
    }
}
