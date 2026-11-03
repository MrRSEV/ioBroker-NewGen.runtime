using System;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Core.Deployment;
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
            ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? legacy = null)
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
        }

        public SemanticStateMatrix Matrix { get; }

        public IRoleService RoleService => Matrix.RoleService;

        public SystemMessageBus SystemBus { get; }

        public AdapterRouter Router => _router;

        public AdapterRegistry AdapterRegistry { get; }

        public AdapterInstaller? AdapterInstaller { get; }

        public AdapterPackageInstaller? AdapterPackageInstaller { get; }

        public AdapterRepositoryClient? AdapterRepositoryClient { get; }

        public ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? Legacy { get; }

        public IConfig Config { get; }

        public IRuntimeContext RuntimeContext { get; }

        public RuntimeApiHost ApiHost => RuntimeContext.ApiHost;

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
    }
}
