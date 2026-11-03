using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Adapters.Configuration;
using ioBroker_NewGen.Core.Registry;
using RSEV.Utilities.Logging;
using RSEV.Utilities.Processes;

namespace ioBroker_NewGen.Core.Bridge
{
    /// <summary>
    /// Verwaltet isolierte Node.js-Prozesse f�r Legacy-ioBroker-Adapter.
    /// Nutzt die Prozess-Infrastruktur aus RSEV.Utilities
    /// (<see cref="NodeProcess"/>, <see cref="RuntimeProcessController"/>)
    /// und erg�nzt Crash-Recovery sowie periodische Heartbeat-/Health-Checks.
    /// </summary>
    public sealed class NodeAdapterRuntime : IAsyncDisposable
    {
        private readonly RuntimeProcessController _processController;
        private readonly AdapterRegistry _adapterRegistry;
        private readonly ILogger _logger;
        private readonly TimeSpan _crashRestartDelay;
        private readonly TimeSpan _healthCheckInterval;

        private readonly ConcurrentDictionary<string, string> _processIdByAdapterId = new();
        private readonly ConcurrentDictionary<string, ProcessSpec> _processSpecByAdapterId = new();
        private readonly SemaphoreSlim _healthCheckGate = new(1, 1);
        private Timer? _healthCheckTimer;
        private CancellationTokenSource? _monitoringCts;

        public NodeAdapterRuntime(
            RuntimeProcessController processController,
            AdapterRegistry adapterRegistry,
            ILogger logger,
            TimeSpan? crashRestartDelay = null,
            TimeSpan? healthCheckInterval = null)
        {
            _processController = processController ?? throw new ArgumentNullException(nameof(processController));
            _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _crashRestartDelay = crashRestartDelay ?? TimeSpan.FromMilliseconds(2000);
            _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromMilliseconds(5000);
        }

        /// <summary>
        /// Startet einen isolierten Node.js-Adapterprozess, registriert ihn in der
        /// <see cref="AdapterRegistry"/> (Typ <see cref="Routing.AdapterType.IoBroker"/>)
        /// und beim <see cref="RuntimeProcessController"/>.
        /// </summary>
        /// <param name="name">Sprechender Adaptername (z. B. "mqtt").</param>
        /// <param name="scriptPath">Pfad zum Node.js-Einstiegsskript des Adapters.</param>
        /// <param name="arguments">Optionale Kommandozeilen-Argumente.</param>
        public async Task<AdapterRegistration> StartAdapterAsync(
            string name,
            string scriptPath,
            string[]? arguments = null,
            string? instanceId = null,
            string? adapterDirectory = null)
        {
            var registration = instanceId is null
                ? _adapterRegistry.Register(name, Routing.AdapterType.IoBroker)
                : _adapterRegistry.Get(instanceId)
                    ?? _adapterRegistry.RegisterWithId(name, Routing.AdapterType.IoBroker, instanceId);
            if (_processIdByAdapterId.ContainsKey(registration.Id))
            {
                return registration;
            }

            registration.Metadata["scriptPath"] = scriptPath;
            _adapterRegistry.UpdateStatus(registration.Id, AdapterStatus.Connecting);

            var arguments1 = arguments ?? Array.Empty<string>();
            var configs = _adapterRegistry.Configs;
            configs.RegisterSchema(name, AdapterConfigSchema.FromDirectory(adapterDirectory ?? Path.GetDirectoryName(scriptPath)));
            if (configs.HasConfig(name) && !arguments1.Contains("--configFile"))
            {
                var configFile = configs.EnsureFile(registration.Id, name);
                arguments1 = arguments1.Concat(new[] { "--configFile", configFile }).ToArray();
            }

            var spec = new ProcessSpec(registration.Id, scriptPath, arguments1);
            var process = CreateProcess(spec);

            _processController.Register(process);
            _processIdByAdapterId[registration.Id] = process.ProcessId;
            _processSpecByAdapterId[registration.Id] = spec;

            var started = await process.StartAsync();
            _adapterRegistry.UpdateStatus(registration.Id, started ? AdapterStatus.Connected : AdapterStatus.Faulted);

            if (!started)
            {
                _logger.LogError($"[NodeRuntime] Adapter '{registration.Id}' konnte nicht gestartet werden (Skript: {scriptPath}).");
            }

            return registration;
        }

        /// <summary>
        /// Startet alle Legacy-Adapter, die in einem Adapterverzeichnis einen
        /// üblichen Node-Einstiegspunkt besitzen. Die Adapter erhalten die
        /// Bridge-Verbindungsdaten als Kommandozeilenargumente.
        /// </summary>
        public async Task<IReadOnlyCollection<AdapterRegistration>> StartDirectoryAsync(
            string directory,
            string bridgeHost,
            int bridgePort)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<AdapterRegistration>();
            }

            var registrations = new List<AdapterRegistration>();
            foreach (var adapterDirectory in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var scriptPath = LegacyAdapterPackage.ResolveEntry(adapterDirectory);
                if (scriptPath is null)
                {
                    _logger.LogWarning($"[NodeRuntime] Kein Einstiegsskript in '{adapterDirectory}' gefunden.");
                    continue;
                }

                var name = Path.GetFileName(adapterDirectory);
                registrations.Add(await StartAdapterAsync(
                    name,
                    scriptPath,
                    new[] { "--host", bridgeHost, "--port", bridgePort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--name", name },
                    adapterDirectory: adapterDirectory));
            }

            return registrations;
        }

        public bool IsRunning(string adapterId) => _processIdByAdapterId.ContainsKey(adapterId);

        /// <summary>
        /// Stoppt den Node.js-Prozess eines Adapters und aktualisiert dessen Status.
        /// </summary>
        public async Task<bool> StopAdapterAsync(string adapterId)
        {
            if (!_processIdByAdapterId.TryGetValue(adapterId, out var processId))
            {
                return false;
            }

            var stopped = await _processController.StopAsync(processId);
            _processController.Unregister(processId);
            _processIdByAdapterId.TryRemove(adapterId, out _);
            _processSpecByAdapterId.TryRemove(adapterId, out _);
            _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Disconnected);
            return stopped;
        }

        /// <summary>
        /// Startet den periodischen Health-Check/Crash-Recovery-Timer.
        /// Prozesse, die nicht mehr "gesund" sind, werden automatisch neu gestartet.
        /// </summary>
        public void StartHealthMonitoring()
        {
            StopHealthMonitoring();
            _monitoringCts = new CancellationTokenSource();
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = new Timer(
                _ => _ = CheckAndRecoverGuardedAsync(_monitoringCts.Token),
                state: null,
                dueTime: _healthCheckInterval,
                period: _healthCheckInterval);
        }

        /// <summary>
        /// Stoppt den Health-Check-Timer.
        /// </summary>
        public void StopHealthMonitoring()
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
            _monitoringCts?.Cancel();
            _monitoringCts?.Dispose();
            _monitoringCts = null;
        }

        /// <summary>
        /// Stoppt alle verwalteten Node.js-Prozesse (z. B. beim Systemstopp).
        /// </summary>
        public async Task StopAllAsync()
        {
            StopHealthMonitoring();

            foreach (var adapterId in _processIdByAdapterId.Keys.ToList())
            {
                await StopAdapterAsync(adapterId);
            }
        }

        private async Task CheckAndRecoverGuardedAsync(CancellationToken cancellationToken)
        {
            var acquired = false;
            try
            {
                acquired = await _healthCheckGate.WaitAsync(0, cancellationToken);
                if (!acquired)
                {
                    return;
                }

                await CheckAndRecoverAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError($"[NodeRuntime] Health-Check-Zyklus fehlgeschlagen: {ex}");
            }
            finally
            {
                if (acquired)
                {
                    _healthCheckGate.Release();
                }
            }
        }

        private async Task CheckAndRecoverAsync(CancellationToken cancellationToken)
        {
            foreach (var (adapterId, processId) in _processIdByAdapterId.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var process = _processController.Get(processId);
                if (process is null)
                {
                    continue;
                }

                bool? healthy;
                try
                {
                    healthy = await process.CheckHealthAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[NodeRuntime] Health-Check f�r Adapter '{adapterId}' fehlgeschlagen: {ex.Message}");
                    healthy = false;
                }

                if (healthy == false)
                {
                    _logger.LogWarning($"[NodeRuntime] Adapter '{adapterId}' unhealthy/abgest�rzt � Neustart nach {_crashRestartDelay.TotalMilliseconds} ms.");
                    _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Faulted);

                    await Task.Delay(_crashRestartDelay, cancellationToken);

                    var restarted = await RestartProcessAsync(adapterId, processId, cancellationToken);
                    _adapterRegistry.UpdateStatus(adapterId, restarted ? AdapterStatus.Connected : AdapterStatus.Faulted);

                    if (restarted)
                    {
                        _logger.LogInfo($"[NodeRuntime] Adapter '{adapterId}' erfolgreich neu gestartet.");
                    }
                    else
                    {
                        _logger.LogError($"[NodeRuntime] Neustart von Adapter '{adapterId}' fehlgeschlagen.");
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAllAsync();
            _healthCheckGate.Dispose();
        }

        private NodeProcess CreateProcess(ProcessSpec spec)
        {
            var process = new NodeProcess(spec.AdapterId, spec.ScriptPath, spec.Arguments, _logger);
            process.OnStdErr += (_, line) => _logger.LogWarning($"[Node:{spec.AdapterId}] {line}");
            return process;
        }

        private async Task<bool> RestartProcessAsync(
            string adapterId,
            string oldProcessId,
            CancellationToken cancellationToken)
        {
            if (!_processSpecByAdapterId.TryGetValue(adapterId, out var spec))
            {
                return false;
            }

            var oldProcess = _processController.Get(oldProcessId);
            if (oldProcess is not null)
            {
                try
                {
                    await oldProcess.StopAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"[NodeRuntime] Alter Prozess '{oldProcessId}' konnte nicht beendet werden: {ex.Message}");
                }
            }

            _processController.Unregister(oldProcessId);
            var replacement = CreateProcess(spec);
            _processController.Register(replacement);
            _processIdByAdapterId[adapterId] = replacement.ProcessId;
            var started = await replacement.StartAsync(CancellationToken.None);
            return started || _adapterRegistry.Get(adapterId)?.Status == AdapterStatus.Connected;
        }

        private sealed record ProcessSpec(string AdapterId, string ScriptPath, string[] Arguments);
    }
}
