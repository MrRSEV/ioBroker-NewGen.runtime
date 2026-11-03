using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ioBroker_NewGen.Core.Localization
{
    /// <summary>
    /// Standardimplementierung. Sprachdateien (<c>Localization/xx_YY.json</c>) sind in den Host
    /// eingebettet; Dateien im Ordner <c>Localization</c> neben der Anwendung überschreiben Schlüssel.
    /// </summary>
    public sealed class LocalizationService : ILocalizationService
    {
        public const string DefaultLanguage = "de_DE";
        public const string DefaultFallbackLanguage = "en_GB";
        public const string LanguageNameKey = "language.name";

        private const string ResourcePrefix = "Localization.";

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _catalogs =
            new(StringComparer.OrdinalIgnoreCase);

        private volatile string _language = DefaultLanguage;
        private volatile string _fallback = DefaultFallbackLanguage;
        private CultureInfo _culture = CultureInfo.InvariantCulture;

        public event Action<string>? LanguageChanged;

        public string Language => _language;

        public string FallbackLanguage => _fallback;

        public CultureInfo Culture => _culture;

        public IReadOnlyList<LanguageInfo> AvailableLanguages =>
            _catalogs
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new LanguageInfo(
                    pair.Key,
                    pair.Value.TryGetValue(LanguageNameKey, out var name) ? name : pair.Key))
                .ToArray();

        public static LocalizationService Create(string? language = null, string? fallbackLanguage = null, string? overrideDirectory = null)
        {
            var service = new LocalizationService();
            service.LoadEmbedded(typeof(LocalizationService).Assembly);
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
            {
                service.LoadDirectory(overrideDirectory);
            }

            service.SetFallbackLanguage(fallbackLanguage ?? DefaultFallbackLanguage);
            service.SetLanguage(language ?? DefaultLanguage);
            return service;
        }

        /// <summary>Normiert "de-de", "DE_de" oder "de" zu "de_DE" bzw. "de".</summary>
        public static string? NormalizeCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var parts = code.Trim().Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var language = parts[0].ToLowerInvariant();
            return parts.Length > 1 ? language + "_" + parts[^1].ToUpperInvariant() : language;
        }

        public int LoadEmbedded(Assembly assembly)
        {
            var loaded = 0;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                    !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = NormalizeCode(name[ResourcePrefix.Length..^5]);
                if (code is null)
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is not null && TryMerge(code, stream))
                {
                    loaded++;
                }
            }

            return loaded;
        }

        public int LoadDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            var loaded = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var code = NormalizeCode(Path.GetFileNameWithoutExtension(file));
                if (code is null)
                {
                    continue;
                }

                using var stream = File.OpenRead(file);
                if (TryMerge(code, stream))
                {
                    loaded++;
                }
            }

            return loaded;
        }

        public bool SetLanguage(string language)
        {
            var resolved = ResolveLanguage(language);
            if (resolved is null)
            {
                return false;
            }

            var changed = !string.Equals(_language, resolved, StringComparison.Ordinal);
            _language = resolved;
            _culture = ToCulture(resolved);
            if (changed)
            {
                LanguageChanged?.Invoke(resolved);
            }

            return true;
        }

        public bool SetFallbackLanguage(string language)
        {
            var resolved = ResolveLanguage(language);
            if (resolved is null)
            {
                return false;
            }

            _fallback = resolved;
            return true;
        }

        public string? ResolveLanguage(string? language)
        {
            var normalized = NormalizeCode(language);
            if (normalized is null)
            {
                return null;
            }

            var exact = _catalogs.Keys.FirstOrDefault(key => string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }

            var prefix = normalized.Split('_')[0] + "_";
            return _catalogs.Keys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public bool TryTranslate(string key, out string value, string? language = null)
        {
            value = key;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var active = ResolveLanguage(language) ?? _language;
            foreach (var code in new[] { active, _fallback })
            {
                if (_catalogs.TryGetValue(code, out var catalog) && catalog.TryGetValue(key, out var text))
                {
                    value = text;
                    return true;
                }
            }

            return false;
        }

        public string Translate(string key, string? language = null)
        {
            TryTranslate(key, out var value, language);
            return value;
        }

        public string Format(string key, params object?[] args)
        {
            var text = Translate(key);
            if (args is null || args.Length == 0)
            {
                return text;
            }

            try
            {
                return string.Format(_culture, text, args);
            }
            catch (FormatException)
            {
                return text;
            }
        }

        public IReadOnlyDictionary<string, string> GetAll(string? language = null)
        {
            var active = ResolveLanguage(language) ?? _language;
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var code in new[] { _fallback, active })
            {
                if (_catalogs.TryGetValue(code, out var catalog))
                {
                    foreach (var pair in catalog)
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            return merged;
        }

        public void Register(string language, IReadOnlyDictionary<string, string> translations)
        {
            ArgumentNullException.ThrowIfNull(translations);
            var code = NormalizeCode(language) ?? throw new ArgumentException("Ungültiger Sprachcode.", nameof(language));
            var catalog = _catalogs.GetOrAdd(code, _ => new ConcurrentDictionary<string, string>(StringComparer.Ordinal));
            foreach (var pair in translations)
            {
                catalog[pair.Key] = pair.Value;
            }
        }

        public int Extend(string language, IReadOnlyDictionary<string, string> translations)
        {
            ArgumentNullException.ThrowIfNull(translations);
            var resolved = ResolveLanguage(language);
            if (resolved is null || !_catalogs.TryGetValue(resolved, out var catalog))
            {
                return 0;
            }

            var added = 0;
            foreach (var pair in translations)
            {
                if (catalog.TryAdd(pair.Key, pair.Value))
                {
                    added++;
                }
            }

            return added;
        }

        public int ExtendFromJson(string language, string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                var flat = new Dictionary<string, string>(StringComparer.Ordinal);
                Flatten(document.RootElement, string.Empty, flat);
                return Extend(language, flat);
            }
            catch (JsonException)
            {
                return 0;
            }
        }

        public int ExtendFromDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return 0;
            }

            var added = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                added += ExtendFromJson(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
            }

            return added;
        }

        private bool TryMerge(string code, Stream stream)
        {
            try
            {
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                var flat = new Dictionary<string, string>(StringComparer.Ordinal);
                Flatten(document.RootElement, string.Empty, flat);
                Register(code, flat);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> target)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Flatten(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name, target);
                    }

                    break;
                case JsonValueKind.String:
                    target[prefix] = element.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    target[prefix] = element.ToString();
                    break;
            }
        }

        private static CultureInfo ToCulture(string code)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code.Replace('_', '-'));
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }
}
