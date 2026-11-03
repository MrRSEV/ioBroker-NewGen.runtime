using System;
using System.Collections.Generic;
using System.Globalization;

namespace ioBroker_NewGen.Core.Localization
{
    /// <summary>
    /// Eine verfügbare Sprache (Code im Format "de_DE" und ihr eigener Name).
    /// </summary>
    public sealed record LanguageInfo(string Code, string Name);

    /// <summary>
    /// Zentrale Sprachweiche des Hosts. Wird von der Matrix, der AdminUI und über
    /// die HTTP-API (<c>/api/i18n/*</c>) auch von anderen Adaptern genutzt.
    /// </summary>
    public interface ILocalizationService
    {
        string Language { get; }

        string FallbackLanguage { get; }

        CultureInfo Culture { get; }

        IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

        event Action<string>? LanguageChanged;

        /// <summary>Akzeptiert "de_de", "de-DE" oder "de". Liefert false, wenn keine Sprachdatei passt.</summary>
        bool SetLanguage(string language);

        bool SetFallbackLanguage(string language);

        string? ResolveLanguage(string? language);

        /// <summary>Sprache → Rückfallsprache → Schlüssel selbst.</summary>
        string Translate(string key, string? language = null);

        string Format(string key, params object?[] args);

        bool TryTranslate(string key, out string value, string? language = null);

        /// <summary>Alle Übersetzungen, fehlende Schlüssel aus der Rückfallsprache ergänzt.</summary>
        IReadOnlyDictionary<string, string> GetAll(string? language = null);

        /// <summary>Ergänzt Übersetzungen (z. B. Adapter mit eigenem Präfix "adapter.hue.…").</summary>
        void Register(string language, IReadOnlyDictionary<string, string> translations);

        /// <summary>
        /// Erweitert den Katalog einer vorhandenen Sprache um Adapter-Schlüssel. Bestehende
        /// Schlüssel (Host oder andere Adapter) werden nicht überschrieben; unbekannte
        /// Sprachen werden übersprungen. Liefert die Zahl neu hinzugefügter Schlüssel.
        /// </summary>
        int Extend(string language, IReadOnlyDictionary<string, string> translations);

        /// <summary>Wie <see cref="Extend"/>, mit JSON-Text (flach oder verschachtelt).</summary>
        int ExtendFromJson(string language, string json);

        /// <summary>Erweitert aus allen <c>xx_YY.json</c>/<c>xx.json</c>-Dateien eines Ordners.</summary>
        int ExtendFromDirectory(string directory);
    }
}
