using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Deployment
{
    /// <summary>
    /// Prüft und installiert signierte (Hash-basierte) lokale Adapterpakete.
    /// Ein Paket enthält eine <c>adapter.json</c> sowie die in <c>Files</c>
    /// aufgeführten DLL-Dateien.
    /// </summary>
    public sealed class AdapterPackageInstaller
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly AdapterInstaller _installer;

        public AdapterPackageInstaller(AdapterInstaller installer)
        {
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        }

        public async Task<AdapterPackageManifest> ValidateAsync(
            string packageDirectory,
            CancellationToken cancellationToken = default)
        {
            var root = ValidateDirectory(packageDirectory);
            var manifestPath = Path.Combine(root, "adapter.json");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("adapter.json fehlt im Adapterpaket.", manifestPath);
            }

            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<AdapterPackageManifest>(
                stream,
                JsonOptions,
                cancellationToken);
            if (manifest is null)
            {
                throw new InvalidDataException("adapter.json ist leer oder ungültig.");
            }

            manifest.Validate();
            foreach (var entry in manifest.Files)
            {
                var file = ResolvePackageFile(root, entry.Key);
                if (!File.Exists(file))
                {
                    throw new FileNotFoundException($"Datei '{entry.Key}' fehlt im Adapterpaket.", file);
                }

                var expected = NormalizeHash(entry.Value);
                var actual = await ComputeSha256Async(file, cancellationToken);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"SHA-256-Prüfung für '{entry.Key}' fehlgeschlagen.");
                }
            }

            return manifest;
        }

        public async Task<AdapterPackageManifest> InstallAsync(
            string packageDirectory,
            CancellationToken cancellationToken = default)
        {
            var root = ValidateDirectory(packageDirectory);
            var manifest = await ValidateAsync(root, cancellationToken);
            _installer.EnsureLayout();

            foreach (var relativeFile in manifest.Files.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolvePackageFile(root, relativeFile);
                var destination = Path.Combine(_installer.CustomPath, Path.GetFileName(relativeFile));
                File.Copy(source, destination, overwrite: true);
            }

            return manifest;
        }

        public static async Task<AdapterPackageManifest> CreateManifestAsync(
            string packageDirectory,
            string name,
            string version,
            string entryAssembly,
            IReadOnlyCollection<string>? dependencies = null,
            CancellationToken cancellationToken = default)
        {
            var root = ValidateDirectory(packageDirectory);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetFileName(file);
                files[relative] = await ComputeSha256Async(file, cancellationToken);
            }

            var manifest = new AdapterPackageManifest
            {
                Name = name,
                Version = version,
                EntryAssembly = entryAssembly,
                Dependencies = dependencies ?? Array.Empty<string>(),
                Files = files
            };
            manifest.Validate();
            var manifestPath = Path.Combine(root, "adapter.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken);
            return manifest;
        }

        private static string ValidateDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Adapterpaket nicht gefunden: '{path}'.");
            }

            return Path.GetFullPath(path);
        }

        private static string ResolvePackageFile(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException("Adapterpaket enthält einen leeren Dateipfad.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Dateipfad außerhalb des Adapterpakets: '{relativePath}'.");
            }

            return fullPath;
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string NormalizeHash(string hash)
        {
            return new string((hash ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}
