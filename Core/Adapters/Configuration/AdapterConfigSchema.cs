using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ioBroker_NewGen.Core.Adapters.Configuration
{
    /// <summary>
    /// Beschreibung eines Adapterfelds für <see cref="AdapterConfigSchema.FromFields"/>.
    /// </summary>
    public sealed record AdapterConfigField(string Key, string Type, object? Default, string Label, string? Help = null)
    {
        public static AdapterConfigField Text(string key, string label, string defaultValue = "", string? help = null)
            => new(key, "text", defaultValue, label, help);

        public static AdapterConfigField Password(string key, string label, string? help = null)
            => new(key, "password", string.Empty, label, help);

        public static AdapterConfigField Number(string key, string label, long defaultValue, string? help = null)
            => new(key, "number", defaultValue, label, help);

        public static AdapterConfigField Checkbox(string key, string label, bool defaultValue = false, string? help = null)
            => new(key, "checkbox", defaultValue, label, help);
    }

    /// <summary>
    /// Konfigurationsbeschreibung eines Adapters analog zum ioBroker-Original:
    /// <see cref="Native"/> entspricht <c>io-package.json → native</c> (Standardwerte),
    /// <see cref="JsonConfig"/> dem <c>jsonConfig.json</c> für die Admin-Oberfläche.
    /// </summary>
    public sealed class AdapterConfigSchema
    {
        private static readonly JsonDocumentOptions Lenient = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>jsonConfig-Schema (JSON-Text) oder <c>null</c>, dann erzeugt die UI ein Formular aus den Standardwerten.</summary>
        public string? JsonConfig { get; init; }

        /// <summary>Standardwerte je Schlüssel (ohne Adapterpräfix, z. B. <c>bridgeUri</c>).</summary>
        public IReadOnlyDictionary<string, object?> Native { get; init; } = new Dictionary<string, object?>();

        /// <summary>OAuth-Anbieter, über die sich die Admin-Oberfläche in einer isolierten Browser-Sitzung anmelden kann.</summary>
        public IReadOnlyList<OAuthProviderSpec> OAuthProviders { get; init; } = Array.Empty<OAuthProviderSpec>();

        public AdapterConfigSchema WithOAuth(params OAuthProviderSpec[] providers)
            => new() { JsonConfig = JsonConfig, Native = Native, OAuthProviders = providers };

        public static AdapterConfigSchema FromFields(params AdapterConfigField[] fields)
        {
            var items = new JsonObject();
            var native = new Dictionary<string, object?>();
            foreach (var field in fields)
            {
                var item = new JsonObject { ["type"] = field.Type, ["label"] = field.Label };
                if (field.Help is not null)
                {
                    item["help"] = field.Help;
                }

                items[field.Key] = item;
                native[field.Key] = field.Default;
            }

            var root = new JsonObject { ["type"] = "panel", ["items"] = items };
            return new AdapterConfigSchema { JsonConfig = root.ToJsonString(), Native = native };
        }

        /// <summary>
        /// Liest das Schema eines Legacy-Adapters aus dessen Verzeichnis
        /// (<c>io-package.json</c> und <c>admin/jsonConfig.json</c>). Liefert <c>null</c>, wenn nichts Konfigurierbares beschrieben ist.
        /// </summary>
        public static AdapterConfigSchema? FromDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var native = new Dictionary<string, object?>();
            string? adminUi = null;
            if (TryParseFile(Path.Combine(directory, "io-package.json")) is JsonObject package)
            {
                if (package["native"] is JsonObject nativeNode)
                {
                    foreach (var (key, value) in nativeNode)
                    {
                        native[key] = value?.DeepClone();
                    }
                }

                adminUi = (package["common"]?["adminUI"]?["config"] as JsonValue)?.ToString();
            }

            string? jsonConfig = null;
            if (!string.Equals(adminUi, "none", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = new[]
                {
                    Path.Combine(directory, "admin", "jsonConfig.json"),
                    Path.Combine(directory, "admin", "jsonConfig.json5"),
                    Path.Combine(directory, "jsonConfig.json")
                }.FirstOrDefault(File.Exists);
                if (candidate is not null && TryParseFile(candidate) is { } node)
                {
                    jsonConfig = node.ToJsonString();
                }
            }

            return jsonConfig is null && native.Count == 0
                ? null
                : new AdapterConfigSchema { JsonConfig = jsonConfig, Native = native };
        }

        private static JsonNode? TryParseFile(string path)
        {
            try
            {
                return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), null, Lenient) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
