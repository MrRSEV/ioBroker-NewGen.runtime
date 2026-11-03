using System;

namespace ioBroker_NewGen.Core.Bus
{
    /// <summary>
    /// Neutrales Transport-Paket des <see cref="SystemMessageBus"/>. Kennt
    /// weder den konkreten Adaptertyp noch die Semantik des Inhalts � reine
    /// Transportstruktur gem�� Architektur-Vorgabe ("Systembus transportiert
    /// Payloads ohne Kenntnis des Adaptertyps").
    /// </summary>
    /// <param name="AdapterId">ID des sendenden/zugeh�rigen Adapters (Format "Name.id", siehe TCP-Bridge-Handshake).</param>
    /// <param name="Address">Logische Zieladresse, z. B. eine State-ID ("temp.livingroom").</param>
    /// <param name="Payload">Rohdaten des Frames.</param>
    /// <param name="ValueType">Typbezeichnung des Payloads (z. B. "double", "bool", "json").</param>
    /// <param name="Kind">Art des Frames.</param>
    /// <param name="Timestamp">Erzeugungszeitpunkt (UTC).</param>
    /// <param name="Metadata">Zus�tzliche, optionale Metadaten.</param>
    public sealed class BusFrame : EventArgs
    {
        public BusFrame(
            string adapterId,
            string address,
            ReadOnlyMemory<byte> payload,
            string valueType,
            BusFrameKind kind,
            DateTime timestamp,
            BusFrameMetadata metadata)
        {
            AdapterId = adapterId;
            Address = address;
            Payload = payload;
            ValueType = valueType;
            Kind = kind;
            Timestamp = timestamp;
            Metadata = metadata;
        }

        public string AdapterId { get; }

        public string Address { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public string ValueType { get; }

        public BusFrameKind Kind { get; }

        public DateTime Timestamp { get; }

        public BusFrameMetadata Metadata { get; }

        /// <summary>
        /// Erstellt ein <see cref="BusFrame"/> mit aktuellem UTC-Zeitstempel
        /// und leeren Metadaten, sofern nicht anders angegeben.
        /// </summary>
        public static BusFrame Create(
            string adapterId,
            string address,
            ReadOnlyMemory<byte> payload,
            string valueType,
            BusFrameKind kind,
            BusFrameMetadata? metadata = null)
        {
            return new BusFrame(
                adapterId,
                address,
                payload,
                valueType,
                kind,
                DateTime.UtcNow,
                metadata ?? BusFrameMetadata.Empty);
        }
    }
}
