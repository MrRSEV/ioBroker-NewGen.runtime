using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>Beschreibung eines installierten Legacy-Adapters (Node.js).</summary>
    public sealed record LegacyPackageInfo(
        string Name,
        string Version,
        string Title,
        string Description,
        string Entry,
        string Directory,
        bool HasConfig,
        IReadOnlyList<string> Instances,
        IReadOnlyList<string> Warnings);

    /// <summary>Liest Metadaten (io-package.json / package.json) eines Legacy-Adapterverzeichnisses.</summary>
    public static class LegacyAdapterPackage
    {
        private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);
        private static readonly string[] FallbackEntries = { "main.js", "index.js", "adapter.js", Path.Combine("build", "main.js") };

        public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

        /// <summary>Liefert den absoluten Pfad der Startdatei (package.json "main", sonst main/index/adapter.js) oder null.</summary>
        public static string? ResolveEntry(string directory)
        {
            var root = Path.GetFullPath(directory);
            var main = ReadJson(Path.Combine(root, "package.json")) is { } package &&
                       package.TryGetProperty("main", out var mainElement) && mainElement.ValueKind == JsonValueKind.String
                ? mainElement.GetString()
                : null;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(main))
            {
                candidates.Add(main);
            }

            candidates.AddRange(FallbackEntries);
            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(Path.Combine(root, candidate));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    return full;
                }
            }

            return null;
        }

        /// <summary>Liest Name, Version und Titel; liefert null, wenn kein gültiger Adapter erkannt wird.</summary>
        public static LegacyPackageInfo? Read(string directory, IReadOnlyList<string>? instances = null)
        {
            var ioPackage = ReadJson(Path.Combine(directory, "io-package.json"));
            if (ioPackage is null || !ioPackage.Value.TryGetProperty("common", out var common))
            {
                return null;
            }

            var package = ReadJson(Path.Combine(directory, "package.json"));
            var name = Text(common, "name")?.Trim().ToLowerInvariant();
            if (!IsValidName(name))
            {
                return null;
            }

            var version = Text(common, "version") ?? (package is { } p ? Text(p, "version") : null) ?? "0.0.0";
            var title = Localized(common, "titleLang") ?? Text(common, "title") ?? name!;
            var description = Localized(common, "desc") ?? string.Empty;
            var entry = ResolveEntry(directory) ?? string.Empty;
            var hasConfig = File.Exists(Path.Combine(directory, "admin", "jsonConfig.json")) ||
                            File.Exists(Path.Combine(directory, "admin", "jsonConfig.json5")) ||
                            ioPackage.Value.TryGetProperty("native", out var native) && native.ValueKind == JsonValueKind.Object && native.EnumerateObject().Any();

            return new LegacyPackageInfo(
                name!,
                version,
                title,
                description,
                entry,
                directory,
                hasConfig,
                instances ?? Array.Empty<string>(),
                Array.Empty<string>());
        }

        private static string? Localized(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var language in new[] { "de", "en" })
            {
                if (value.TryGetProperty(language, out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }

            foreach (var entry in value.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    return entry.Value.GetString();
                }
            }

            return null;
        }

        private static string? Text(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static JsonElement? ReadJson(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                return document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
