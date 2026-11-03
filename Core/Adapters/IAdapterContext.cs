using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Core.Deployment;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.ApiBuilder;
using RSEV.Utilities.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters
{
    /// <summary>
    /// Begrenzter Host-Kontext für einen CustomAdapter.
    /// </summary>
    public interface IAdapterContext
    {
        SemanticStateMatrix Matrix { get; }

        IRoleService RoleService => Matrix.RoleService;

        ioBroker_NewGen.Core.Localization.ILocalizationService? Localization => Matrix.Localization;

        SystemMessageBus SystemBus { get; }

        AdapterRouter Router { get; }

        AdapterRegistry AdapterRegistry { get; }

        AdapterInstaller? AdapterInstaller { get; }

        AdapterPackageInstaller? AdapterPackageInstaller { get; }

        AdapterRepositoryClient? AdapterRepositoryClient { get; }

        /// <summary>Verwaltung der Legacy-Adapter (Node.js); null, wenn die Bridge nicht läuft.</summary>
        ioBroker_NewGen.Core.Bridge.LegacyAdapterManager? Legacy => null;

        IConfig Config { get; }

        IRuntimeContext RuntimeContext { get; }

        RuntimeApiHost ApiHost { get; }

        Task<bool> StartAdapterAsync(string adapterId, CancellationToken cancellationToken = default);

        Task<bool> StopAdapterAsync(string adapterId, CancellationToken cancellationToken = default);

        /// <summary>Stoppt und startet einen Adapter neu.</summary>
        async Task<bool> RestartAdapterAsync(string adapterId, CancellationToken cancellationToken = default)
        {
            if (!await StopAdapterAsync(adapterId, cancellationToken))
            {
                return false;
            }

            var started = await StartAdapterAsync(adapterId, cancellationToken);
            if (started)
            {
                AdapterRegistry.RecordRestart(adapterId);
            }

            return started;
        }

        void Publish(BusFrame frame);

        /// <summary>Legt eine weitere Instanz des Adapters an und liefert deren ID (oder null).</summary>
        Task<string?> AddInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        /// <summary>Entfernt eine zusätzliche Instanz (nicht die erste, .0).</summary>
        Task<bool> RemoveInstanceAsync(string adapterId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        void Route(BusFrame frame);
    }
}
