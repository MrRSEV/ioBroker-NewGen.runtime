using System;
using System.Text.Json.Serialization;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Leichtgewichtiges Transport-Envelope des TCP-Bridge-Protokolls.
    /// Wird als kompaktes JSON �ber den Draht geschickt (siehe <see cref="BridgeProtocol"/>
    /// f�r das Framing mit 4-Byte-L�ngenpr�fix).
    /// </summary>
    public sealed class BridgeEnvelope
    {
        /// <summary>
        /// Art der Nachricht.
        /// </summary>
        public BridgeMessageType Type { get; set; }

        /// <summary>
        /// Beim Handshake: der vom Adapter gesendete Name (z. B. "mqtt").
        /// Danach: die vom Host vergebene Adapter-ID (Format "Name.id").
        /// </summary>
        public string? AdapterId { get; set; }

        /// <summary>
        /// Logische Zieladresse eines Frames (z. B. State-ID).
        /// </summary>
        public string? Address { get; set; }

        /// <summary>
        /// Typbezeichnung des Payloads (z. B. "double", "bool", "json").
        /// </summary>
        public string? ValueType { get; set; }

        /// <summary>
        /// Art des transportierten Frames (siehe <see cref="ioBroker_NewGen.Core.Bus.BusFrameKind"/>),
        /// als int �bertragen, um das Bus-Modell nicht direkt an den Draht zu koppeln.
        /// </summary>
        public int FrameKind { get; set; }

        /// <summary>
        /// Payload, Base64-kodiert (kompaktes JSON-Protokoll statt reinem Bin�rformat).
        /// </summary>
        public string? PayloadBase64 { get; set; }

        /// <summary>
        /// Zus�tzliche Metadaten (Key/Value), z. B. correlationId.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Fehlertext, nur bei <see cref="BridgeMessageType.Error"/> gesetzt.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Zeitstempel der Nachricht (UTC).
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
