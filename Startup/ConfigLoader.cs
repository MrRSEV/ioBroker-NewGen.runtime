using RSEV.Utilities.Runtime;
using System.Globalization;
using System.Text.Json;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// L�dt die TOML-Konfiguration in das RSEV.Utilities-Konfigurationsregister.
    /// </summary>
    internal static class ConfigLoader
    {
        public static async Task LoadIfExistsAsync(IRuntimeContext rtx)
        {
            var configPath = ConfigCreator.ConfigPath;
            if (!File.Exists(configPath))
            {
                rtx.Logger.LogWarning($"Keine Konfiguration gefunden: {configPath}");
                return;
            }

            var lines = await File.ReadAllLinesAsync(configPath);
            var section = string.Empty;
            var firstRunLineFound = false;
            var firstRunWasTrue = false;

            // Defaults für Konfigurationen aus älteren Projektständen. Werte
            // aus der Datei überschreiben diese Defaults beim anschließenden Parse.
            rtx.Config.Set("admin.adminEnabled", true);
            rtx.Config.Set("admin.adminPort", 8080);
            rtx.Config.Set("admin.adminRequireAuthentication", true);
            rtx.Config.Set("admin.adminToken", string.Empty);
            rtx.Config.Set("adapter.customAdapterDirectory", "Adapters/custom");
            rtx.Config.Set("adapter.adapterRootPath", "Adapters");
            rtx.Config.Set("adapter.enabledAdapters", new[] { "admin" });
            rtx.Config.Set("adapter.requireSignedPackages", false);
            rtx.Config.Set("adapter.trustedPackagePublicKeyPem", string.Empty);
            rtx.Config.Set("adapter.legacyAutoStart", false);
            rtx.Config.Set("adapter.adapterAutoStart", true);
            rtx.Config.Set("localization.language", "de_de");
            rtx.Config.Set("localization.fallbackLanguage", "en_GB");
            rtx.Config.Set("system.setupDone", false);
            rtx.Config.Set("system.temperatureUnit", "°C");
            rtx.Config.Set("system.currency", "€");
            rtx.Config.Set("system.dateFormat", "DD.MM.YYYY");
            rtx.Config.Set("system.isFloatComma", true);
            rtx.Config.Set("system.firstDayOfWeek", "monday");
            rtx.Config.Set("system.country", string.Empty);
            rtx.Config.Set("system.city", string.Empty);
            rtx.Config.Set("system.address", string.Empty);
            rtx.Config.Set("system.latitude", string.Empty);
            rtx.Config.Set("system.longitude", string.Empty);

            foreach (var sourceLine in lines)
            {
                var line = StripComment(sourceLine).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var valueText = line[(separatorIndex + 1)..].Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                var qualifiedKey = string.IsNullOrEmpty(section) ? key : $"{section}.{key}";
                var value = ParseValue(valueText);
                if (value is string text && text.StartsWith("System.", StringComparison.Ordinal) && text.EndsWith("[]", StringComparison.Ordinal))
                {
                    // Reparatur: ältere Speicherläufe haben Arrays als Typnamen geschrieben.
                    continue;
                }
                rtx.Config.Set(qualifiedKey, value);

                if (string.Equals(qualifiedKey, "node.firstrun", StringComparison.OrdinalIgnoreCase))
                {
                    firstRunLineFound = true;
                    firstRunWasTrue = value is bool enabled && enabled;
                }
            }

            // Ein vorhandenes Konfigurationsdokument bedeutet, dass der Erststart
            // abgeschlossen ist. Dies wird sowohl im RuntimeContext als auch im Register
            // und dauerhaft in der Datei vermerkt.
            rtx.Set("HasRunBefore", true);
            rtx.Config.Set("node.firstrun", false);

            if (!firstRunLineFound || firstRunWasTrue)
            {
                await SetFirstRunFalseInFileAsync(configPath, lines);
            }

            rtx.Logger.LogInfo($"Konfiguration geladen: {configPath}");
        }

        private static object ParseValue(string value)
        {
            if (bool.TryParse(value, out var boolean))
            {
                return boolean;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longInteger))
            {
                return longInteger;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
            }

            if (value.StartsWith('"') && value.EndsWith('"'))
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }

            return value;
        }

        private static string StripComment(string line)
        {
            var inString = false;
            var escaped = false;

            for (var index = 0; index < line.Length; index++)
            {
                var current = line[index];
                if (current == '"' && !escaped)
                {
                    inString = !inString;
                }
                else if (current == '#' && !inString)
                {
                    return line[..index];
                }

                escaped = current == '\\' && !escaped;
                if (current != '\\')
                {
                    escaped = false;
                }
            }

            return line;
        }

        private static async Task SetFirstRunFalseInFileAsync(string configPath, string[] lines)
        {
            var updatedLines = new List<string>(lines.Length + 2);
            var currentSection = string.Empty;
            var firstRunLineUpdated = false;
            var nodeSectionFound = false;

            foreach (var sourceLine in lines)
            {
                var trimmedLine = sourceLine.Trim();
                if (trimmedLine.StartsWith('[') && trimmedLine.EndsWith(']'))
                {
                    if (string.Equals(currentSection, "node", StringComparison.OrdinalIgnoreCase) && !firstRunLineUpdated)
                    {
                        updatedLines.Add("firstrun = false");
                        firstRunLineUpdated = true;
                    }

                    currentSection = trimmedLine[1..^1].Trim();
                    nodeSectionFound |= string.Equals(currentSection, "node", StringComparison.OrdinalIgnoreCase);
                    updatedLines.Add(sourceLine);
                    continue;
                }

                if (string.Equals(currentSection, "node", StringComparison.OrdinalIgnoreCase) &&
                    trimmedLine.StartsWith("firstrun", StringComparison.OrdinalIgnoreCase) &&
                    trimmedLine.Contains('='))
                {
                    updatedLines.Add("firstrun = false");
                    firstRunLineUpdated = true;
                    continue;
                }

                updatedLines.Add(sourceLine);
            }

            if (string.Equals(currentSection, "node", StringComparison.OrdinalIgnoreCase) && !firstRunLineUpdated)
            {
                updatedLines.Add("firstrun = false");
                firstRunLineUpdated = true;
            }

            if (!nodeSectionFound)
            {
                updatedLines.Add("[node]");
                updatedLines.Add("firstrun = false");
            }

            await File.WriteAllLinesAsync(configPath, updatedLines);
        }
    }
}
