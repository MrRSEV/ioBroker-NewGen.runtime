using System;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Core.Deployment;
using ioBroker_NewGen.Core.Web;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.ApiBuilder;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Core.Adapters
{
    internal sealed class CustomAdapterContext : IAdapterContext
    {
        private readonly AdapterRouter _router;

        public CustomAdapterContext(
            SemanticStateMatrix matrix,
            SystemMessageBus systemBus,
            AdapterRouter router,
            AdapterRegistry adapterRegistry,
            IRuntimeContext runtimeContext,
            CustomAdapterRuntime adapterRuntime,
            AdapterInstaller? adapterInstaller,
            AdapterPackageInstaller? adapterPackageInstaller,
            AdapterRepositoryClient? adapterRepositoryClient,
            ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? legacy = null,
            IWebEndpointRegistry? webEndpoints = null)
        {
            Legacy = legacy;
            Matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
            SystemBus = systemBus ?? throw new ArgumentNullException(nameof(systemBus));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            AdapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
            AdapterInstaller = adapterInstaller;
            AdapterPackageInstaller = adapterPackageInstaller;
            AdapterRepositoryClient = adapterRepositoryClient;
            RuntimeContext = runtimeContext ?? throw new ArgumentNullException(nameof(runtimeContext));
            Config = RuntimeContext.Config;
            AdapterRuntime = adapterRuntime ?? throw new ArgumentNullException(nameof(adapterRuntime));
            WebEndpoints = webEndpoints ?? throw new ArgumentNullException(nameof(webEndpoints));
        }

        public SemanticStateMatrix Matrix { get; }

        public IRoleService RoleService => Matrix.RoleService;

        public ioBroker_NewGen.Core.Localization.ILocalizationService? Localization => Matrix.Localization;

        public SystemMessageBus SystemBus { get; }

        public AdapterRouter Router => _router;

        public AdapterRegistry AdapterRegistry { get; }

        public IWebEndpointRegistry WebEndpoints { get; }

        public AdapterInstaller? AdapterInstaller { get; }

        public AdapterPackageInstaller? AdapterPackageInstaller { get; }

        public AdapterRepositoryClient? AdapterRepositoryClient { get; }

        public ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? Legacy { get; }

        public IConfig Config { get; }

        public IRuntimeContext RuntimeContext { get; }

        public RuntimeApiHost ApiHost => DenyWebHostAccess(string.Empty);

        private CustomAdapterRuntime AdapterRuntime { get; }

        public Task<bool> StartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            return IsLegacyInstance(adapterId)
                ? Legacy!.StartInstanceAsync(adapterId, cancellationToken)
                : AdapterRuntime.StartAdapterAsync(adapterId, cancellationToken);
        }

        public Task<bool> StopAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            return IsLegacyInstance(adapterId)
                ? Legacy!.StopInstanceAsync(adapterId, cancellationToken)
                : AdapterRuntime.StopAdapterAsync(adapterId, cancellationToken);
        }

        public Task<bool> RestartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            return IsLegacyInstance(adapterId)
                ? Legacy!.RestartInstanceAsync(adapterId, cancellationToken)
                : AdapterRuntime.RestartAdapterAsync(adapterId, cancellationToken);
        }

        private bool IsLegacyInstance(string adapterId) => Legacy?.IsInstance(adapterId) == true;

        public Task<string?> AddInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            return AdapterRegistry.Get(adapterId)?.Type == AdapterType.IoBroker || (Legacy?.IsInstalled(adapterId) ?? false)
                ? Legacy?.AddInstanceAsync(adapterId, cancellationToken) ?? Task.FromResult<string?>(null)
                : AdapterRuntime.AddInstanceAsync(adapterId, cancellationToken);
        }

        public Task<bool> RemoveInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            return IsLegacyInstance(adapterId)
                ? Legacy!.RemoveInstanceAsync(adapterId, cancellationToken)
                : AdapterRuntime.RemoveInstanceAsync(adapterId, cancellationToken);
        }

        public void Publish(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            SystemBus.Publish(frame);
        }

        public void Route(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            _router.Route(frame, AdapterType.Custom);
        }

        internal IAdapterContext CreateScoped(string adapterId)
            => new AdapterScopedContext(this, adapterId);

        private RuntimeApiHost DenyWebHostAccess(string adapterId)
        {
            var exception = new ioBroker_NewGen.Core.Bridge.WebHostServiceAccessException(
                $"Adapterinstanz '{adapterId}' darf RuntimeApiHost/WebHostService.cs nicht verwenden. " +
                "Für HTTP/WebSocket-Endpunkte ist IWebEndpointRegistry vorgesehen.");
            AdapterRuntime.ReportForbiddenWebHostAccess(adapterId, exception);
            throw exception;
        }

        private sealed class AdapterScopedContext : IAdapterContext
        {
            private readonly CustomAdapterContext _owner;
            private readonly string _adapterId;

            public AdapterScopedContext(CustomAdapterContext owner, string adapterId)
            {
                _owner = owner;
                _adapterId = adapterId;
            }

            public SemanticStateMatrix Matrix => _owner.Matrix;
            public IRoleService RoleService => _owner.RoleService;
            public ioBroker_NewGen.Core.Localization.ILocalizationService? Localization => _owner.Localization;
            public SystemMessageBus SystemBus => _owner.SystemBus;
            public AdapterRouter Router => _owner.Router;
            public AdapterRegistry AdapterRegistry => _owner.AdapterRegistry;
            public IWebEndpointRegistry WebEndpoints => _owner.WebEndpoints;
            public AdapterInstaller? AdapterInstaller => _owner.AdapterInstaller;
            public AdapterPackageInstaller? AdapterPackageInstaller => _owner.AdapterPackageInstaller;
            public AdapterRepositoryClient? AdapterRepositoryClient => _owner.AdapterRepositoryClient;
            public ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? Legacy => _owner.Legacy;
            public IConfig Config => _owner.Config;
            public IRuntimeContext RuntimeContext => _owner.CreateScopedRuntimeContext(_adapterId);
            public RuntimeApiHost ApiHost => _owner.DenyWebHostAccess(_adapterId);

            public Task<bool> StartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
                => _owner.StartAdapterAsync(adapterId, cancellationToken);

            public Task<bool> StopAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
                => _owner.StopAdapterAsync(adapterId, cancellationToken);

            public void Publish(BusFrame frame) => _owner.Publish(frame);

            public Task<string?> AddInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
                => _owner.AddInstanceAsync(adapterId, cancellationToken);

            public Task<bool> RemoveInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
                => _owner.RemoveInstanceAsync(adapterId, cancellationToken);

            public void Route(BusFrame frame) => _owner.Route(frame);
        }

        private IRuntimeContext CreateScopedRuntimeContext(string adapterId)
            => new AdapterRuntimeContextProxy(this, adapterId, RuntimeContext);

        private sealed class AdapterRuntimeContextProxy : IRuntimeContext
        {
            private readonly CustomAdapterContext _owner;
            private readonly string _adapterId;
            private readonly IRuntimeContext _inner;

            public AdapterRuntimeContextProxy(CustomAdapterContext owner, string adapterId, IRuntimeContext inner)
            {
                _owner = owner;
                _adapterId = adapterId;
                _inner = inner;
            }

            public IConfig Config => _inner.Config;
            public RSEV.Utilities.Logging.ILogger Logger => _inner.Logger;
            public RSEV.Utilities.Loading.AssemblyLoader AssemblyLoader => _inner.AssemblyLoader;
            public RSEV.Utilities.Loading.TypeDiscovery TypeDiscovery => _inner.TypeDiscovery;
            public RSEV.Utilities.Plugins.PluginRegistry PluginRegistry => _inner.PluginRegistry;
            public RSEV.Utilities.Messaging.MessageBusRegistry MessageBusRegistry => _inner.MessageBusRegistry;
            public RSEV.Utilities.Processes.RuntimeProcessController ProcessController => _inner.ProcessController;
            public RuntimeApiHost ApiHost => _owner.DenyWebHostAccess(_adapterId);
            public IDictionary<string, object> GlobalState => _inner.GlobalState;
            public bool IsRunning { get => _inner.IsRunning; set => _inner.IsRunning = value; }

            public void Set(string key, object value) => _inner.Set(key, value);

            public T Get<T>(string key) => _inner.Get<T>(key);

            public bool TryGet<T>(string key, out T value) => _inner.TryGet(key, out value);
        }
    }
}
