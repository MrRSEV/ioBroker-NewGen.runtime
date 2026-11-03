using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Adapters.Configuration;
using ioBroker_NewGen.Core.Deployment;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Startup;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Verwaltet Legacy-Adapter (Node.js): Installation aus ZIP-Paketen, Instanzen
    /// (<c>name.N</c>) und deren Lifecycle. Die Instanzliste steht in
    /// <c>adapter.legacyInstances</c> der <c>config.toml</c>.
    /// </summary>
    public sealed class LegacyAdapterManager
    {
        public const string InstancesKey = "adapter.legacyInstances";

        private const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
        private const int MaxEntries = 200_000;
        private static readonly Regex InstanceIdPattern = new(@"^(?<name>[a-z0-9][a-z0-9_-]*)\.(?<index>\d+)$", RegexOptions.Compiled);

        private readonly NodeAdapterRuntime _node;
        private readonly AdapterInstaller _installer;
        private readonly AdapterRegistry _registry;
        private readonly IConfig _config;
        private readonly ILogger _logger;
        private readonly string _bridgeHost;
        private readonly int _bridgePort;
        private readonly string _adapterCoreDirectory;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public LegacyAdapterManager(
            NodeAdapterRuntime node,
            AdapterInstaller installer,
            AdapterRegistry registry,
            IConfig config,
            ILogger logger,
            string bridgeHost,
            int bridgePort,
            string? adapterCoreDirectory = null)
        {
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bridgeHost = bridgeHost;
            _bridgePort = bridgePort;
            _adapterCoreDirectory = adapterCoreDirectory ?? Path.Combine(AppContext.BaseDirectory, "Legacy", "adapter-core");
        }

        public IReadOnlyList<LegacyPackageInfo> GetInstalled()
        {
            var instances = ReadInstances();
            var result = new List<LegacyPackageInfo>();
            foreach (var directory in _installer.GetInstalledLegacyAdapters())
            {
                var info = LegacyAdapterPackage.Read(directory);
                if (info is null)
                {
                    continue;
                }

                result.Add(info with
                {
                    Instances = instances.Where(id => string.Equals(SplitId(id)?.Name, info.Name, StringComparison.Ordinal)).ToArray()
                });
            }

            return result.OrderBy(info => info.Name, StringComparer.Ordinal).ToList();
        }

        public bool IsInstance(string id) => ReadInstances().Contains(id, StringComparer.OrdinalIgnoreCase);

        public bool IsInstalled(string name) => FindPackage(name) is not null;

        /// <summary>Installiert (oder aktualisiert) einen Adapter aus einem ZIP-Archiv.</summary>
        public async Task<LegacyPackageInfo> InstallAsync(Stream zip, CancellationToken cancellationToken = default, Action<string>? progress = null)
        {
            progress?.Invoke("Warte auf andere Adapter-Operationen...");
            await _gate.WaitAsync(cancellationToken);
            var staging = Path.Combine(_installer.RootPath, "staging", Guid.NewGuid().ToString("N"));
            try
            {
                _installer.EnsureLayout();
                Directory.CreateDirectory(staging);
                progress?.Invoke("Entpacke ZIP-Archiv...");
                Extract(zip, staging, progress);
                _logger.LogInfo("[Legacy] ZIP entpackt.");
                progress?.Invoke("ZIP entpackt. Prüfe io-package.json...");

                var root = FindPackageRoot(staging)
                    ?? throw new InvalidDataException("Im ZIP wurde keine io-package.json gefunden – kein ioBroker-Adapter.");
                var info = LegacyAdapterPackage.Read(root)
                    ?? throw new InvalidDataException("io-package.json ist ungültig (common.name fehlt oder enthält unzulässige Zeichen).");
                if (string.Equals(info.Name, "admin", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Der Name 'admin' ist reserviert.");
                }

                if (string.IsNullOrEmpty(info.Entry))
                {
                    throw new InvalidDataException(
                        "Die Startdatei des Adapters fehlt (package.json \"main\", z. B. build/main.js). Der Adapter muss gebaut sein (npm run build).");
                }

                progress?.Invoke($"Adapter erkannt: {info.Name} {info.Version}");
                var warnings = new List<string>();
                if (!Directory.Exists(Path.Combine(root, "node_modules")) && HasDependencies(root))
                {
                    warnings.Add("Der Ordner node_modules fehlt im ZIP – Abhängigkeiten des Adapters sind nicht installiert (npm install).");
                    progress?.Invoke("WARNUNG: " + warnings[^1]);
                }

                var target = Path.Combine(_installer.IoBrokerPath, info.Name);
                var wasRunning = ReadInstances()
                    .Where(id => SplitId(id)?.Name == info.Name && _node.IsRunning(id))
                    .ToList();
                foreach (var id in wasRunning)
                {
                    progress?.Invoke($"Stoppe laufende Instanz {id}...");
                    await _node.StopAdapterAsync(id);
                }

                if (Directory.Exists(target))
                {
                    progress?.Invoke("Entferne vorherige Version...");
                    Directory.Delete(target, recursive: true);
                }

                _logger.LogInfo($"[Legacy] Entpackt, verschiebe '{info.Name}' nach {target}...");
                progress?.Invoke($"Installiere nach {target}...");
                MoveWithRetry(root, target);
                progress?.Invoke("Patche Adapter-Core und registriere Konfigurationsschema...");
                PatchAdapterCore(target, warnings);
                _registry.Configs.RegisterSchema(info.Name, AdapterConfigSchema.FromDirectory(target));
                _logger.LogInfo($"[Legacy] Adapter '{info.Name}' {info.Version} installiert.");

                foreach (var id in wasRunning)
                {
                    progress?.Invoke($"Starte Instanz {id} neu...");
                    await StartCoreAsync(id);
                }

                progress?.Invoke($"Adapter '{info.Name}' {info.Version} erfolgreich installiert.");

                var installed = LegacyAdapterPackage.Read(target)!;
                return installed with { Warnings = warnings, Instances = ReadInstances().Where(id => SplitId(id)?.Name == info.Name).ToArray() };
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Legacy] Installation fehlgeschlagen: {ex.Message}");
                throw;
            }
            finally
            {
                TryDelete(staging);
                _gate.Release();
            }
        }

        /// <summary>Entfernt den Adapter samt aller Instanzen und deren Konfiguration.</summary>
        public async Task<bool> UninstallAsync(string name, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var package = FindPackage(name);
                if (package is null)
                {
                    return false;
                }

                foreach (var id in ReadInstances().Where(id => SplitId(id)?.Name == name).ToList())
                {
                    await RemoveCoreAsync(id);
                }

                Directory.Delete(package.Directory, recursive: true);
                _logger.LogInfo($"[Legacy] Adapter '{name}' deinstalliert.");
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Legt die nächste freie Instanz (<c>name.N</c>) an. Akzeptiert Adaptername oder Instanz-ID.</summary>
        public async Task<string?> AddInstanceAsync(string nameOrId, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var name = SplitId(nameOrId)?.Name ?? nameOrId;
                var package = FindPackage(name);
                if (package is null)
                {
                    return null;
                }

                var instances = ReadInstances();
                var index = 0;
                while (instances.Contains($"{name}.{index}", StringComparer.OrdinalIgnoreCase))
                {
                    index++;
                }

                var id = $"{name}.{index}";
                Persist(instances.Append(id));
                EnsureRegistered(id, package);
                return id;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RemoveInstanceAsync(string id, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await RemoveCoreAsync(id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> StartInstanceAsync(string id, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await StartCoreAsync(id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> StopInstanceAsync(string id, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await StopCoreAsync(id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> RestartInstanceAsync(string id, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!await StopCoreAsync(id))
                {
                    return false;
                }

                var started = await StartCoreAsync(id);
                if (started)
                {
                    _registry.RecordRestart(id);
                }

                return started;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Registriert alle gespeicherten Instanzen und startet sie. Mit <paramref name="adoptInstalled"/>
        /// erhalten installierte Adapter ohne Instanz automatisch die Instanz <c>name.0</c>.
        /// </summary>
        public async Task StartAllAsync(bool adoptInstalled = false, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var packages = _installer.GetInstalledLegacyAdapters()
                    .Select(directory => LegacyAdapterPackage.Read(directory))
                    .Where(info => info is not null)
                    .Select(info => info!)
                    .ToList();
                var instances = ReadInstances().ToList();
                if (adoptInstalled)
                {
                    foreach (var package in packages.Where(p => !instances.Any(id => SplitId(id)?.Name == p.Name)))
                    {
                        instances.Add($"{package.Name}.0");
                    }

                    Persist(instances);
                }

                foreach (var id in instances)
                {
                    var package = packages.FirstOrDefault(p => p.Name == SplitId(id)?.Name);
                    if (package is null)
                    {
                        _logger.LogWarning($"[Legacy] Instanz '{id}' kann nicht gestartet werden (Adapter nicht installiert).");
                        continue;
                    }

                    EnsureRegistered(id, package);
                    try
                    {
                        await StartCoreAsync(id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[Legacy] Start von '{id}' fehlgeschlagen: {ex.Message}");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<bool> StartCoreAsync(string id)
        {
            var parts = SplitId(id);
            if (parts is null || !IsInstance(id))
            {
                return false;
            }

            var package = FindPackage(parts.Name);
            if (package is null || string.IsNullOrEmpty(package.Entry))
            {
                return false;
            }

            EnsureRegistered(id, package);
            var registration = await _node.StartAdapterAsync(
                parts.Name,
                package.Entry,
                new[]
                {
                    "--host", _bridgeHost,
                    "--port", _bridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--name", parts.Name,
                    "--instance", parts.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                id,
                package.Directory);
            return registration.Status is AdapterStatus.Connected or AdapterStatus.Connecting;
        }

        private async Task<bool> StopCoreAsync(string id)
        {
            if (!IsInstance(id))
            {
                return false;
            }

            await _node.StopAdapterAsync(id);
            return true;
        }

        private async Task<bool> RemoveCoreAsync(string id)
        {
            if (!IsInstance(id))
            {
                return false;
            }

            await _node.StopAdapterAsync(id);
            _registry.Unregister(id);
            _registry.Configs.Delete(id);
            Persist(ReadInstances().Where(entry => !string.Equals(entry, id, StringComparison.OrdinalIgnoreCase)));
            return true;
        }

        private void EnsureRegistered(string id, LegacyPackageInfo package)
        {
            var parts = SplitId(id)!;
            _registry.Configs.RegisterSchema(parts.Name, AdapterConfigSchema.FromDirectory(package.Directory));
            if (_registry.Get(id) is null)
            {
                var registration = _registry.RegisterWithId(parts.Name, Routing.AdapterType.IoBroker, id);
                registration.Metadata["version"] = package.Version;
            }
        }

        private LegacyPackageInfo? FindPackage(string name)
        {
            if (!LegacyAdapterPackage.IsValidName(name))
            {
                return null;
            }

            var directory = Path.Combine(_installer.IoBrokerPath, name);
            return Directory.Exists(directory) ? LegacyAdapterPackage.Read(directory) : null;
        }

        private IReadOnlyList<string> ReadInstances()
        {
            try
            {
                return _config.Get<string[]>(InstancesKey)?
                    .Where(entry => SplitId(entry) is not null).ToArray() ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void Persist(IEnumerable<string> ids)
        {
            var array = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            _config.Set(InstancesKey, array);
            try
            {
                ConfigFileWriter.Update(
                    Path.Combine(AppContext.BaseDirectory, "config.toml"),
                    new Dictionary<string, object?> { [InstancesKey] = array });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Legacy] Instanzliste konnte nicht gespeichert werden: {ex.Message}");
            }
        }

        private sealed record IdParts(string Name, int Index);

        private static IdParts? SplitId(string? id)
        {
            var match = id is null ? null : InstanceIdPattern.Match(id);
            return match is { Success: true } && int.TryParse(match.Groups["index"].Value, out var index)
                ? new IdParts(match.Groups["name"].Value, index)
                : null;
        }

        private static bool HasDependencies(string directory)
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(directory, "package.json"));
                return text.Contains("\"dependencies\"", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
        }

        // Virenscanner/Indexer/OneDrive sperren frisch entpackte Ordner kurzzeitig.
        private static void MoveWithRetry(string source, string target)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(source, target);
                    return;
                }
                catch (Exception ex) when (attempt < 8 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(1500 * attempt);
                }
            }
        }

        private static void Extract(Stream zip, string destination, Action<string>? progress = null)
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
            var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            long total = 0;
            var count = 0;
            var entries = archive.Entries.Count;
            var lastPercent = -1;
            foreach (var entry in archive.Entries)
            {
                if (++count > MaxEntries)
                {
                    throw new InvalidDataException("Das ZIP enthält zu viele Dateien.");
                }

                var percent = count * 10 / Math.Max(1, entries) * 10;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Invoke($"Entpacke... {percent}% ({count}/{entries} Einträge)");
                }

                var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Unzulässiger Pfad im ZIP: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                total += entry.Length;
                if (total > MaxUncompressedBytes)
                {
                    throw new InvalidDataException("Das ZIP ist entpackt zu groß.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        /// <summary>Sucht das Verzeichnis mit io-package.json (Wurzel oder bis zu zwei Ebenen tiefer).</summary>
        private static string? FindPackageRoot(string staging)
        {
            var level = new List<string> { staging };
            for (var depth = 0; depth <= 2 && level.Count > 0; depth++)
            {
                var hit = level.FirstOrDefault(directory => File.Exists(Path.Combine(directory, "io-package.json")));
                if (hit is not null)
                {
                    return hit;
                }

                level = level.SelectMany(directory => Directory.EnumerateDirectories(directory))
                    .Where(directory => !string.Equals(Path.GetFileName(directory), "node_modules", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return null;
        }

        /// <summary>Ersetzt @iobroker/adapter-core durch den Bridge-Ersatz, damit der Adapter ohne js-controller läuft.</summary>
        private void PatchAdapterCore(string adapterDirectory, List<string> warnings)
        {
            var nodeModules = Path.Combine(adapterDirectory, "node_modules");
            var packageJson = Path.Combine(adapterDirectory, "package.json");
            var usesCore = File.Exists(packageJson) && File.ReadAllText(packageJson).Contains("@iobroker/adapter-core", StringComparison.Ordinal);
            if (!usesCore && !Directory.Exists(nodeModules))
            {
                return;
            }

            if (!Directory.Exists(_adapterCoreDirectory))
            {
                warnings.Add("Der Ersatz für @iobroker/adapter-core (Legacy\\adapter-core) wurde nicht gefunden – der Adapter nutzt das Original und verbindet sich nicht mit der Bridge.");
                return;
            }

            var existing = Directory.Exists(nodeModules)
                ? Directory.EnumerateDirectories(nodeModules, "adapter-core", SearchOption.AllDirectories)
                    .Where(directory => string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "@iobroker", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();
            if (existing.Count == 0 && usesCore)
            {
                existing.Add(Path.Combine(nodeModules, "@iobroker", "adapter-core"));
            }

            foreach (var directory in existing)
            {
                TryDelete(directory);
                CopyDirectory(_adapterCoreDirectory, directory);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Aufräumen ist optional; gesperrte Dateien werden beim nächsten Lauf ersetzt.
            }
        }
    }
}
