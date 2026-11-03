using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ioBroker_NewGen.Core.Adapters.Configuration
{
    public sealed class AdapterConfigException : Exception
    {
        public AdapterConfigException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Verwaltet die Einstellungen (<c>native</c>) je Adapterinstanz. Gespeichert wird pro Instanz eine Datei
    /// <c>&lt;instanceId&gt;.json</c>; fehlende Werte werden aus den Standardwerten des Schemas ergänzt.
    /// </summary>
    public sealed class AdapterConfigService
    {
        public const string SecretMask = "__SECRET__";

        private readonly ConcurrentDictionary<string, AdapterConfigSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _ioLock = new();
        private readonly string _directory;

        public AdapterConfigService(string? directory = null)
        {
            _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "data", "adapter-config");
        }

        public void RegisterSchema(string adapterName, AdapterConfigSchema? schema)
        {
            if (schema is not null)
            {
                _schemas[adapterName] = schema;
            }
        }

        public AdapterConfigSchema? GetSchema(string adapterName)
            => _schemas.TryGetValue(adapterName, out var schema) ? schema : null;

        public bool HasConfig(string adapterName)
            => GetSchema(adapterName) is { } s && (s.JsonConfig is not null || s.Native.Count > 0);

        /// <summary>Gespeicherte Werte über den Standardwerten; Standardwerte allein, wenn nichts gespeichert wurde.</summary>
        public JsonObject GetValues(string instanceId, string adapterName)
        {
            var result = new JsonObject();
            if (GetSchema(adapterName) is { } schema)
            {
                foreach (var (key, value) in schema.Native)
                {
                    result[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
                }
            }

            var path = PathFor(instanceId);
            lock (_ioLock)
            {
                if (File.Exists(path) && TryRead(path) is { } stored)
                {
                    foreach (var (key, value) in stored)
                    {
                        result[key] = value?.DeepClone();
                    }
                }
            }

            return result;
        }

        /// <summary>Namen aller Felder vom Typ <c>password</c> im jsonConfig.</summary>
        public ISet<string> SecretKeys(string adapterName)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (GetSchema(adapterName)?.JsonConfig is { } json && JsonNode.Parse(json) is { } root)
            {
                CollectKeys(root, keys, secretsOnly: true);
            }

            return keys;
        }

        /// <summary>Alle im Schema bekannten Schlüssel (Native + jsonConfig-Attribute).</summary>
        public ISet<string> KnownKeys(string adapterName)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (GetSchema(adapterName) is { } schema)
            {
                keys.UnionWith(schema.Native.Keys);
                if (schema.JsonConfig is { } json && JsonNode.Parse(json) is { } root)
                {
                    CollectKeys(root, keys, secretsOnly: false);
                }
            }

            return keys;
        }

        /// <summary>
        /// Speichert die Werte. Unbekannte Schlüssel und Typabweichungen werden abgelehnt;
        /// leere/maskierte Passwortfelder behalten ihren bisherigen Wert.
        /// </summary>
        public JsonObject Save(string instanceId, string adapterName, JsonObject values)
        {
            var known = KnownKeys(adapterName);
            var secrets = SecretKeys(adapterName);
            var current = GetValues(instanceId, adapterName);
            var defaults = GetSchema(adapterName)?.Native;

            foreach (var (key, value) in values)
            {
                if (!known.Contains(key))
                {
                    throw new AdapterConfigException($"Unbekannte Einstellung '{key}'.");
                }

                if (secrets.Contains(key) && (value is null || value.ToString() is "" or SecretMask))
                {
                    continue;
                }

                if (defaults is not null && defaults.TryGetValue(key, out var def) && !TypeCompatible(def, value))
                {
                    throw new AdapterConfigException($"Einstellung '{key}' hat einen ungültigen Typ.");
                }

                current[key] = value?.DeepClone();
            }

            var path = PathFor(instanceId);
            Directory.CreateDirectory(_directory);
            lock (_ioLock)
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
            }

            return current;
        }

        /// <summary>Stellt sicher, dass die Datei der Instanz existiert, und liefert ihren Pfad (für Legacy-Adapter).</summary>
        public string EnsureFile(string instanceId, string adapterName)
        {
            var path = PathFor(instanceId);
            var values = GetValues(instanceId, adapterName);
            Directory.CreateDirectory(_directory);
            lock (_ioLock)
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, values.ToJsonString());
                File.Move(temp, path, overwrite: true);
            }

            return path;
        }

        /// <summary>Kopie der Werte mit maskierten Passwortfeldern für die Oberfläche.</summary>
        public JsonObject Mask(string adapterName, JsonObject values)
        {
            var secrets = SecretKeys(adapterName);
            var copy = (JsonObject)values.DeepClone();
            foreach (var key in secrets)
            {
                if (copy.TryGetPropertyValue(key, out var v) && v is not null && v.ToString().Length > 0)
                {
                    copy[key] = SecretMask;
                }
            }

            return copy;
        }

        public void Delete(string instanceId)
        {
            var path = PathFor(instanceId);
            lock (_ioLock)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private string PathFor(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)
                || instanceId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
                || instanceId.Contains(".."))
            {
                throw new AdapterConfigException("Ungültige Instanz-ID.");
            }

            return Path.Combine(_directory, instanceId + ".json");
        }

        private static JsonObject? TryRead(string path)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }

        private static bool TypeCompatible(object? def, JsonNode? value)
        {
            if (def is null || value is null)
            {
                return true;
            }

            var kind = value.GetValueKind();
            return def switch
            {
                string => kind is JsonValueKind.String,
                bool => kind is JsonValueKind.True or JsonValueKind.False,
                JsonNode n => n.GetValueKind() == kind || n.GetValueKind() == JsonValueKind.Null,
                _ when IsNumber(def) => kind is JsonValueKind.Number,
                _ => true
            };
        }

        private static bool IsNumber(object o) => o is byte or short or ushort or int or uint or long or ulong or float or double or decimal;

        // jsonConfig: Felder stehen als "attr" an Items (Array/Objekt "items"); Objekt-Form nutzt den Schlüssel als Attribut.
        private static void CollectKeys(JsonNode node, ISet<string> keys, bool secretsOnly)
        {
            if (node is JsonObject obj)
            {
                var type = (obj["type"] as JsonValue)?.ToString();
                if (obj["attr"] is JsonValue attr && attr.ToString() is { Length: > 0 } name
                    && (!secretsOnly || type == "password"))
                {
                    keys.Add(name);
                }

                if (obj["items"] is JsonObject itemsObj)
                {
                    foreach (var (key, child) in itemsObj)
                    {
                        if (child is JsonObject c && c["type"] is not null && c["attr"] is null
                            && (!secretsOnly || (c["type"] as JsonValue)?.ToString() == "password")
                            && (c["type"] as JsonValue)?.ToString() is not ("panel" or "tabs" or "header" or "divider" or "staticText"))
                        {
                            keys.Add(key);
                        }

                        if (child is not null)
                        {
                            CollectKeys(child, keys, secretsOnly);
                        }
                    }
                }
                else if (obj["items"] is JsonNode itemsNode)
                {
                    CollectKeys(itemsNode, keys, secretsOnly);
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var child in arr)
                {
                    if (child is not null)
                    {
                        CollectKeys(child, keys, secretsOnly);
                    }
                }
            }
        }
    }
}
