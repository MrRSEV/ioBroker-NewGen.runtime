using System;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Deployment
{
    /// <summary>
    /// Beschreibt ein installierbares Adapterpaket. Die Dateihashes schützen
    /// die Installation vor unvollständigen oder veränderten Paketinhalten.
    /// </summary>
    public sealed class AdapterPackageManifest
    {
        public string Name { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string EntryAssembly { get; init; } = string.Empty;

        public IReadOnlyCollection<string> Dependencies { get; init; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> Files { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) ||
                Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                Name.Contains(Path.DirectorySeparatorChar) ||
                Name.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new InvalidOperationException("Adapterpaket enthält keinen gültigen Namen.");
            }

            if (string.IsNullOrWhiteSpace(Version))
            {
                throw new InvalidOperationException("Adapterpaket enthält keine Version.");
            }

            if (string.IsNullOrWhiteSpace(EntryAssembly) ||
                !EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("EntryAssembly muss auf eine DLL zeigen.");
            }

            if (!Files.ContainsKey(EntryAssembly))
            {
                throw new InvalidOperationException("EntryAssembly fehlt in der Dateiliste des Pakets.");
            }

            foreach (var file in Files.Keys)
            {
                if (!string.Equals(Path.GetFileName(file), file, StringComparison.OrdinalIgnoreCase) ||
                    !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file, "ioBroker-NewGen.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file, "RSEV.Utilities.dll", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Adapterpaket darf die Datei '{file}' nicht installieren.");
                }
            }
        }
    }
}
