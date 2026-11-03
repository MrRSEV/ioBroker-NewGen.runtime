using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RSEV.Utilities.Configuration;

namespace ioBroker_NewGen.Core.Adapters.Configuration
{
    /// <summary>
    /// Instanzbezogene Sicht auf die Konfiguration: Schlüssel wie <c>hue.bridgeUri</c> werden zuerst aus den
    /// Einstellungen der Adapterinstanz gelesen, alles andere kommt aus der globalen Konfiguration.
    /// Die Werte sind ein Schnappschuss vom Start des Adapters.
    /// </summary>
    public sealed class InstanceConfig : IConfig
    {
        private readonly IConfig _inner;
        private readonly string _prefix;
        private readonly JsonObject _values;

        public InstanceConfig(IConfig inner, string adapterName, JsonObject values)
        {
            _inner = inner;
            _prefix = adapterName + ".";
            _values = values;
        }

        public T Get<T>(string key)
        {
            return TryScoped(key, out _, out var node) ? Convert<T>(node!, key) : _inner.Get<T>(key);
        }

        public void Set<T>(string key, T value)
        {
            if (TryScoped(key, out var scoped, out _))
            {
                _values[scoped] = JsonSerializer.SerializeToNode(value);
                return;
            }

            _inner.Set(key, value);
        }

        public void Load(string path) => _inner.Load(path);

        public Task LoadAsync(string path) => _inner.LoadAsync(path);

        public void Save(string path) => _inner.Save(path);

        public Task SaveAsync(string path) => _inner.SaveAsync(path);

        private bool TryScoped(string key, out string scopedKey, out JsonNode? node)
        {
            scopedKey = key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) ? key[_prefix.Length..] : key;
            return _values.TryGetPropertyValue(scopedKey, out node) && node is not null;
        }

        private static T Convert<T>(JsonNode node, string key)
        {
            if (typeof(T) == typeof(string))
            {
                var text = node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                    ? value.GetValue<string>()
                    : node.ToJsonString();
                return (T)(object)text;
            }

            try
            {
                return node.Deserialize<T>()!;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Wert von '{key}' hat nicht den erwarteten Typ {typeof(T).Name}.", ex);
            }
        }
    }
}
