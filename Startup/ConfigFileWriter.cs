using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Schreibt einzelne Schlüssel gezielt in die vorhandene config.toml, ohne das
    /// Dokument neu zu serialisieren (Sektionen, Kommentare und Arrays bleiben erhalten).
    /// </summary>
    public static class ConfigFileWriter
    {
        private static readonly object Gate = new();

        public static void Update(string path, IReadOnlyDictionary<string, object?> values)
        {
            lock (Gate)
            {
                var lines = File.Exists(path)
                    ? File.ReadAllLines(path, Encoding.UTF8).ToList()
                    : new List<string>();
                var pending = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);

                var section = string.Empty;
                for (var index = 0; index < lines.Count && pending.Count > 0; index++)
                {
                    var line = lines[index].Trim();
                    if (line.Length > 1 && line[0] == '[' && line[^1] == ']')
                    {
                        section = line[1..^1].Trim();
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0 || line[0] == '#')
                    {
                        continue;
                    }

                    var key = line[..separator].Trim();
                    var qualified = string.IsNullOrEmpty(section) ? key : section + "." + key;
                    if (pending.TryGetValue(qualified, out var value))
                    {
                        lines[index] = key + " = " + Format(value);
                        pending.Remove(qualified);
                    }
                }

                foreach (var (qualified, value) in pending)
                {
                    var dot = qualified.IndexOf('.');
                    if (dot <= 0)
                    {
                        lines.Insert(0, qualified + " = " + Format(value));
                        continue;
                    }

                    var sectionName = qualified[..dot];
                    var key = qualified[(dot + 1)..];
                    var entry = key + " = " + Format(value);
                    var header = lines.FindIndex(l => string.Equals(l.Trim(), "[" + sectionName + "]", StringComparison.OrdinalIgnoreCase));
                    if (header < 0)
                    {
                        // Flache Dateien (ohne Sektionen) bekommen den voll qualifizierten Schlüssel.
                        if (!lines.Any(l => l.TrimStart().StartsWith('[')))
                        {
                            lines.Add(qualified + " = " + Format(value));
                            continue;
                        }

                        if (lines.Count > 0 && lines[^1].Length > 0)
                        {
                            lines.Add(string.Empty);
                        }

                        lines.Add("[" + sectionName + "]");
                        lines.Add(entry);
                        continue;
                    }

                    var insertAt = header + 1;
                    while (insertAt < lines.Count && !lines[insertAt].TrimStart().StartsWith('['))
                    {
                        insertAt++;
                    }

                    while (insertAt - 1 > header && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                    {
                        insertAt--;
                    }

                    lines.Insert(insertAt, entry);
                }

                var temp = path + ".tmp";
                File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
        }

        private static string Format(object? value) => value switch
        {
            null => "\"\"",
            bool b => b ? "true" : "false",
            int or long or short or byte => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            string[] array => JsonSerializer.Serialize(array),
            IEnumerable<string> list => JsonSerializer.Serialize(list.ToArray()),
            _ => JsonSerializer.Serialize(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }
}
