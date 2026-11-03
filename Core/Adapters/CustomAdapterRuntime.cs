using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Core.Deployment;
using RSEV.Utilities.Loading;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.Runtime;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Core.Adapters
{
    /// <summary>
    /// Lädt und verwaltet host-interne C#-Adapter über die RSEV.Utilities-
    /// AssemblyLoader-Infrastruktur.
    /// </summary>
    public sealed class CustomAdapterRuntime : IAsyncDisposable
    {
        private readonly AssemblyLoader _assemblyLoader;
        private readonly AdapterRegistry _adapterRegistry;
        private readonly IAdapterContext _adapterContext;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, IAdapter> _adapters = new();
        private readonly object _subscriptionLock = new();
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private readonly Action<BusFrame> _dispatchHandler;
        private bool _subscribed;
        private int _disposed;

        public CustomAdapterRuntime(
            AssemblyLoader assemblyLoader,
            SemanticStateMatrix matrix,
            SystemMessageBus systemBus,
            AdapterRouter router,
            AdapterRegistry adapterRegistry,
            ILogger logger,
            IRuntimeContext runtimeContext,
            AdapterInstaller? adapterInstaller = null,
            AdapterPackageInstaller? adapterPackageInstaller = null,
            AdapterRepositoryClient? adapterRepositoryClient = null,
            ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? legacy = null)
        {
            _assemblyLoader = assemblyLoader ?? throw new ArgumentNullException(nameof(assemblyLoader));
            _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _adapterContext = new CustomAdapterContext(
                matrix,
                systemBus,
                router,
                adapterRegistry,
                runtimeContext,
                this,
                adapterInstaller,
                adapterPackageInstaller,
                adapterRepositoryClient,
                legacy);
            _dispatchHandler = DispatchCommandFrame;
            systemBus.Subscribe("*", _dispatchHandler);
            _subscribed = true;
        }

        public IReadOnlyCollection<IAdapter> Adapters => _adapters.Values.ToList();

        /// <summary>
        /// Lädt alle Assemblies eines Verzeichnisses und erzeugt darin gefundene
        /// IAdapter-Implementierungen über parameterlose Konstruktoren.
        /// </summary>
        public IReadOnlyCollection<IAdapter> LoadAdapters(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                _logger.LogWarning($"[CustomAdapter] Verzeichnis nicht gefunden: '{directory}'.");
                return Array.Empty<IAdapter>();
            }

            var assemblies = _assemblyLoader.LoadAssembliesFromDirectory(directory);
            var loaded = new List<IAdapter>();

            foreach (var assembly in assemblies)
            {
                foreach (var type in _assemblyLoader.FindTypesImplementing<IAdapter>(assembly))
                {
                    if (type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }

                    try
                    {
                        var adapter = _assemblyLoader.CreateInstance<IAdapter>(type);
                        Register(adapter);
                        loaded.Add(adapter);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[CustomAdapter] Laden von '{type.FullName}' fehlgeschlagen: {ex.Message}");
                    }
                }
            }

            return loaded;
        }

        public AdapterRegistration Register(IAdapter adapter, string? instanceId = null)
        {
            ArgumentNullException.ThrowIfNull(adapter);

            if (adapter.Type != AdapterType.Custom)
            {
                throw new InvalidOperationException($"Adapter '{adapter.Name}' muss den Typ Custom besitzen.");
            }

            var registration = instanceId is null
                ? _adapterRegistry.Register(adapter.Name, AdapterType.Custom)
                : _adapterRegistry.RegisterWithId(adapter.Name, AdapterType.Custom, instanceId);
            adapter.Id = registration.Id;
            var schema = adapter.ConfigSchema
                ?? Configuration.AdapterConfigSchema.FromDirectory(System.IO.Path.GetDirectoryName(adapter.GetType().Assembly.Location));
            _adapterRegistry.Configs.RegisterSchema(adapter.Name, schema);
            registration.InstanceReference = adapter;
            var dependencies = adapter.Dependencies ?? Array.Empty<string>();
            if (dependencies.Count > 0)
            {
                registration.Metadata["dependencies"] = string.Join(",", dependencies);
            }
            _adapters[registration.Id] = adapter;
            return registration;
        }

        public async Task<bool> StartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                return await StartAdapterCoreAsync(adapterId, cancellationToken);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task<bool> StartAdapterCoreAsync(string adapterId, CancellationToken cancellationToken)
        {
            var resolvedId = ResolveAdapterId(adapterId);
            return resolvedId is not null &&
                   await StartAdapterWithDependenciesAsync(resolvedId, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
        }

        private void RegisterAdapterTranslations(IAdapter adapter)
        {
            var localization = _adapterContext.Matrix.Localization;
            if (localization is null)
            {
                return;
            }

            try
            {
                foreach (var (language, texts) in adapter.Translations)
                {
                    localization.Extend(language, texts);
                }

                var directory = adapter.LocalizationDirectory;
                if (string.IsNullOrWhiteSpace(directory))
                {
                    var location = adapter.GetType().Assembly.Location;
                    directory = string.IsNullOrEmpty(location)
                        ? null
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(location)!, "Localization");
                }

                if (directory is not null)
                {
                    localization.ExtendFromDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[CustomAdapter] Übersetzungen von '{adapter.Id}' konnten nicht geladen werden: {ex.Message}");
            }
        }

        private string? ResolveAdapterId(string adapterReference)
        {
            if (string.IsNullOrWhiteSpace(adapterReference))
            {
                return null;
            }

            if (_adapters.ContainsKey(adapterReference) && _adapterRegistry.Get(adapterReference) is not null)
            {
                return adapterReference;
            }

            return _adapters.Values
                .Select(candidate => _adapterRegistry.Get(candidate.Id))
                .Where(candidate => candidate is not null)
                .OrderBy(candidate => candidate!.Id, StringComparer.Ordinal)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate!.Name, adapterReference, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        private async Task<bool> StartAdapterWithDependenciesAsync(
            string adapterId,
            HashSet<string> visiting,
            CancellationToken cancellationToken)
        {
            if (!_adapters.TryGetValue(adapterId, out var adapter) || _adapterRegistry.Get(adapterId) is not { } registration)
            {
                return false;
            }

            if (registration.Status == AdapterStatus.Connected)
            {
                return true;
            }

            if (!visiting.Add(adapterId))
            {
                _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Faulted);
                _logger.LogError($"[CustomAdapter] Abhängigkeitsschleife bei '{adapterId}'.");
                return false;
            }

            foreach (var dependency in adapter.Dependencies ?? Array.Empty<string>())
            {
                var dependencyId = ResolveAdapterId(dependency);
                if (dependencyId is null ||
                    !await StartAdapterWithDependenciesAsync(dependencyId, visiting, cancellationToken))
                {
                    _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Faulted);
                    _logger.LogError($"[CustomAdapter] Abhängigkeit '{dependency}' für '{adapterId}' konnte nicht gestartet werden.");
                    visiting.Remove(adapterId);
                    return false;
                }
            }

            _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Connecting);
            try
            {
                RegisterAdapterTranslations(adapter);
                await adapter.InitializeAsync(_adapterContext, cancellationToken);
                await adapter.StartAsync(cancellationToken);
                _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Connected);
                return true;
            }
            catch (Exception ex)
            {
                _adapterRegistry.UpdateStatus(adapterId, AdapterStatus.Faulted);
                _adapterRegistry.RecordError(adapterId, ex.Message);
                _logger.LogError($"[CustomAdapter] Start von '{adapterId}' fehlgeschlagen: {ex}");
                visiting.Remove(adapterId);
                return false;
            }
            finally
            {
                visiting.Remove(adapterId);
            }
        }

        private const string InstancesKey = "adapter.instances";
        private bool _instancesRestored;

        private string[] ReadExtraInstances()
        {
            try
            {
                return _adapterContext.Config.Get<string[]>(InstancesKey)?
                    .Where(entry => !string.IsNullOrWhiteSpace(entry)).ToArray() ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private void PersistExtraInstances(IEnumerable<string> ids)
        {
            var array = ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            _adapterContext.Config.Set(InstancesKey, array);
            try
            {
                ioBroker_NewGen.Startup.ConfigFileWriter.Update(
                    Path.Combine(AppContext.BaseDirectory, "config.toml"),
                    new Dictionary<string, object?> { [InstancesKey] = array });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[CustomAdapter] Instanzliste konnte nicht gespeichert werden: {ex.Message}");
            }
        }

        private IAdapter? FindTemplate(string name)
        {
            return _adapters.Values
                .Where(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private void RestoreInstances()
        {
            if (_instancesRestored)
            {
                return;
            }

            _instancesRestored = true;
            foreach (var id in ReadExtraInstances())
            {
                var dot = id.LastIndexOf('.');
                if (dot <= 0 || _adapterRegistry.Exists(id))
                {
                    continue;
                }

                var template = FindTemplate(id[..dot]);
                if (template is null || string.Equals(template.Name, "admin", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"[CustomAdapter] Instanz '{id}' kann nicht wiederhergestellt werden (Adapter fehlt).");
                    continue;
                }

                try
                {
                    Register((IAdapter)Activator.CreateInstance(template.GetType())!, id);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[CustomAdapter] Instanz '{id}' konnte nicht erzeugt werden: {ex.Message}");
                }
            }
        }

        /// <summary>Legt eine weitere Instanz (Name.N) des Adapters an und merkt sie sich in der Konfiguration.</summary>
        public async Task<string?> AddInstanceAsync(string adapterReference, CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                var baseId = ResolveAdapterId(adapterReference);
                if (baseId is null || !_adapters.TryGetValue(baseId, out var template) ||
                    string.Equals(template.Name, "admin", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var index = 0;
                while (_adapterRegistry.Exists($"{template.Name}.{index}"))
                {
                    index++;
                }

                var id = $"{template.Name}.{index}";
                Register((IAdapter)Activator.CreateInstance(template.GetType())!, id);
                PersistExtraInstances(ReadExtraInstances().Append(id));
                return id;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        /// <summary>Stoppt und löscht eine zusätzliche Instanz samt Konfiguration. Die erste Instanz (.0) bleibt bestehen.</summary>
        public async Task<bool> RemoveInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                var resolvedId = ResolveAdapterId(adapterId);
                if (resolvedId is null || resolvedId.EndsWith(".0", StringComparison.Ordinal) ||
                    !_adapters.TryGetValue(resolvedId, out var adapter))
                {
                    return false;
                }

                if (_adapterRegistry.Get(resolvedId)?.Status is AdapterStatus.Connected or AdapterStatus.Connecting)
                {
                    await StopAdapterCoreAsync(resolvedId, cancellationToken);
                }

                _adapters.TryRemove(resolvedId, out _);
                _adapterRegistry.Unregister(resolvedId);
                _adapterRegistry.Configs.Delete(resolvedId);
                PersistExtraInstances(ReadExtraInstances().Where(id => !string.Equals(id, resolvedId, StringComparison.OrdinalIgnoreCase)));
                return true;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task<int> StartAllAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                RestoreInstances();
                var started = 0;
                var configuredAdapters = ReadEnabledAdapterReferences();
                var adapterIds = configuredAdapters is null
                    ? _adapters.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray()
                    : configuredAdapters
                        .Select(ResolveAdapterId)
                        .Where(id => id is not null)
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();

                foreach (var adapterId in adapterIds)
                {
                    if (await StartAdapterWithDependenciesAsync(
                            adapterId,
                            new HashSet<string>(StringComparer.Ordinal),
                            cancellationToken))
                    {
                        started++;
                    }
                }

                return started;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private IReadOnlyCollection<string>? ReadEnabledAdapterReferences()
        {
            try
            {
                var configured = _adapterContext.Config.Get<string[]>("adapter.enabledAdapters");
                return configured is null
                    ? Array.Empty<string>()
                    : configured.Where(reference => !string.IsNullOrWhiteSpace(reference)).ToArray();
            }
            catch
            {
                // Kein Aktivierungsfilter in älteren Konfigurationen: aus
                // Kompatibilitätsgründen werden dann alle geladenen Adapter gestartet.
                return null;
            }
        }

        public async Task<bool> StopAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                return await StopAdapterCoreAsync(adapterId, cancellationToken);
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task<bool> StopAdapterCoreAsync(string adapterId, CancellationToken cancellationToken)
        {
            var resolvedId = ResolveAdapterId(adapterId);
            if (resolvedId is null || !_adapters.TryGetValue(resolvedId, out var adapter))
            {
                return false;
            }

            try
            {
                await adapter.StopAsync(cancellationToken);
                _adapterRegistry.UpdateStatus(resolvedId, AdapterStatus.Disconnected);
                return true;
            }
            catch (Exception ex)
            {
                _adapterRegistry.UpdateStatus(resolvedId, AdapterStatus.Faulted);
                _adapterRegistry.RecordError(resolvedId, ex.Message);
                _logger.LogError($"[CustomAdapter] Stop von '{resolvedId}' fehlgeschlagen: {ex}");
                return false;
            }
        }

        /// <summary>Stoppt und startet einen Adapter atomar innerhalb des Lifecycle-Locks.</summary>
        public async Task<bool> RestartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                var resolvedId = ResolveAdapterId(adapterId);
                if (resolvedId is null || !await StopAdapterCoreAsync(resolvedId, cancellationToken))
                {
                    return false;
                }

                var started = await StartAdapterCoreAsync(resolvedId, cancellationToken);
                if (started)
                {
                    _adapterRegistry.RecordRestart(resolvedId);
                }

                return started;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StopAllAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken);
            try
            {
                var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var adapterId in _adapters.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList())
                {
                    await StopAdapterWithDependentsAsync(adapterId, stopped, cancellationToken);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task StopAdapterWithDependentsAsync(
            string adapterId,
            HashSet<string> stopped,
            CancellationToken cancellationToken)
        {
            if (!stopped.Add(adapterId))
            {
                return;
            }

            var dependents = _adapters.Values
                .Where(candidate => candidate.Id != adapterId)
                .Where(candidate => (candidate.Dependencies ?? Array.Empty<string>())
                    .Any(dependency => string.Equals(ResolveAdapterId(dependency), adapterId, StringComparison.OrdinalIgnoreCase)))
                .Select(candidate => candidate.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            foreach (var dependentId in dependents)
            {
                await StopAdapterWithDependentsAsync(dependentId, stopped, cancellationToken);
            }

            await StopAdapterCoreAsync(adapterId, cancellationToken);
        }

        private void DispatchCommandFrame(BusFrame frame)
        {
            if (frame.Kind != BusFrameKind.Command || !_adapters.TryGetValue(frame.AdapterId, out var adapter))
            {
                return;
            }

            try
            {
                adapter.ReceivePayload(frame);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[CustomAdapter] Empfang bei '{frame.AdapterId}' fehlgeschlagen: {ex}");
                _adapterRegistry.UpdateStatus(frame.AdapterId, AdapterStatus.Faulted);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await StopAllAsync();
            }
            finally
            {
                if (_subscribed)
                {
                    lock (_subscriptionLock)
                    {
                        _adapterContext.SystemBus.Unsubscribe("*", _dispatchHandler);
                        _subscribed = false;
                    }
                }

                _lifecycleGate.Dispose();
            }
        }
    }
}
