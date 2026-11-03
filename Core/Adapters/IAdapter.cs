using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Routing;

namespace ioBroker_NewGen.Core.Adapters
{
    /// <summary>
    /// Einheitlicher Vertrag für host-interne C#-Adapter.
    /// </summary>
    public interface IAdapter
    {
        string Name { get; }

        string Id { get; set; }

        AdapterType Type { get; }

        /// <summary>
        /// Adapter-IDs oder Adapternamen, die vor diesem Adapter gestartet
        /// werden müssen. Ein Eintrag darf entweder exakt auf eine ID wie
        /// <c>admin.0</c> oder auf den Namen <c>admin</c> zeigen.
        /// </summary>
        IReadOnlyCollection<string> Dependencies => Array.Empty<string>();

        /// <summary>
        /// Optionale Übersetzungen des Adapters: Sprachcode (z. B. "de_DE", "en", "fr_FR")
        /// auf Schlüssel/Text. Sie erweitern die Host-Sprachdateien, überschreiben sie aber nie.
        /// Empfohlen sind Schlüssel mit Präfix <c>adapter.&lt;name&gt;.</c>.
        /// </summary>
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations
            => new Dictionary<string, IReadOnlyDictionary<string, string>>();

        /// <summary>
        /// Optionaler Ordner mit Sprachdateien (<c>de_DE.json</c>, <c>en_GB.json</c> …).
        /// Ohne Angabe wird ein Ordner <c>Localization</c> neben der Adapter-Assembly verwendet.
        /// </summary>
        string? LocalizationDirectory => null;

        /// <summary>
        /// Optionales Konfigurationsschema (Standardwerte + jsonConfig) für die Admin-Oberfläche.
        /// Ohne Angabe wird <c>io-package.json</c>/<c>jsonConfig.json</c> neben der Assembly gesucht.
        /// </summary>
        ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigSchema? ConfigSchema => null;

        Task InitializeAsync(IAdapterContext context, CancellationToken cancellationToken = default);

        Task StartAsync(CancellationToken cancellationToken = default);

        Task StopAsync(CancellationToken cancellationToken = default);

        Task SendPayloadAsync(BusFrame frame, CancellationToken cancellationToken = default);

        /// <summary>
        /// Kompatibilitätsname aus dem Sprint-1/3-Adaptervertrag. Die
        /// Ausführung bleibt asynchron und delegiert an SendPayloadAsync.
        /// </summary>
        Task SendPayload(BusFrame frame, CancellationToken cancellationToken = default)
            => SendPayloadAsync(frame, cancellationToken);

        void ReceivePayload(BusFrame frame);
    }
}
