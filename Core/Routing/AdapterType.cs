namespace ioBroker_NewGen.Core.Routing
{
    /// <summary>
    /// Typ eines Adapters: bestimmt, wie Frames geroutet werden.
    /// </summary>
    public enum AdapterType
    {
        /// <summary>
        /// Nativer C#-Adapter, l�uft Host-intern, direkt �ber den Systembus.
        /// </summary>
        Custom,

        /// <summary>
        /// Legacy ioBroker-Adapter, l�uft isoliert als Node.js-Prozess,
        /// angebunden �ber die TCP-Bridge.
        /// </summary>
        IoBroker
    }
}
