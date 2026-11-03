using RSEV.Utilities.Lifecycle;
using RSEV.Utilities.Runtime;
using ioBroker_NewGen.Controllers;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Wird bei jedem Start des Systems ausgef�hrt (nach FirstRun, falls Erststart).
    /// </summary>
    public class OnStart : IOnStart
    {
        public async Task OnStartAsync(IRuntimeContext rtx)
        {
            rtx.Logger.LogInfo("System wird gestartet...");
            await ConfigLoader.LoadIfExistsAsync(rtx);

            if (rtx is MainRuntimeContext mainContext)
            {
                var snapshotPath = System.IO.Path.Combine(AppContext.BaseDirectory, "snapshot.jsonl");
                mainContext.InitializeMatrixAndBus(snapshotPath);
                mainContext.InitializeLocalization();
                await mainContext.Matrix.LoadSnapshotAsync();
                mainContext.Matrix.StartCronSnapshot(TimeSpan.FromMinutes(5));
                rtx.Logger.LogInfo("SemanticStateMatrix, SystemBus und AdapterRouter initialisiert.");

                var port = rtx.Config.Get<int>("ipc.ipcPort");
                if (port <= 0)
                {
                    port = 5001;
                }

                var bindHost = rtx.Config.Get<string>("ipc.ipcHost");
                if (string.IsNullOrWhiteSpace(bindHost))
                {
                    bindHost = "127.0.0.1";
                }

                var healthCheckIntervalMs = rtx.Config.Get<int>("adapter.adapterHealthCheckIntervalMs");
                if (healthCheckIntervalMs <= 0)
                {
                    healthCheckIntervalMs = 5000;
                }

                var crashRestartDelayMs = rtx.Config.Get<int>("adapter.adapterCrashRestartDelayMs");
                if (crashRestartDelayMs < 0)
                {
                    crashRestartDelayMs = 2000;
                }

                await mainContext.StartTcpBridgeAsync(port, bindHost);
                mainContext.InitializeNodeRuntime(
                    TimeSpan.FromMilliseconds(crashRestartDelayMs),
                    TimeSpan.FromMilliseconds(healthCheckIntervalMs));

                var adapterRootPath = rtx.Config.Get<string>("adapter.adapterRootPath");
                if (string.IsNullOrWhiteSpace(adapterRootPath))
                {
                    adapterRootPath = "Adapters";
                }

                if (!Path.IsPathRooted(adapterRootPath))
                {
                    adapterRootPath = Path.Combine(AppContext.BaseDirectory, adapterRootPath);
                }

                mainContext.InitializeAdapterDeployment(adapterRootPath);
                mainContext.InitializeLegacyAdapters(bindHost, port);
                mainContext.InitializeBackupService();
                await mainContext.InitializeSecurityAsync();
                mainContext.InitializeCustomAdapterRuntime();
                if (mainContext.LegacyAdapters is not null)
                {
                    await mainContext.LegacyAdapters.StartAllAsync(rtx.Config.Get<bool>("adapter.legacyAutoStart"));
                }
                var customAdapterRuntime = mainContext.CustomAdapterRuntime!;
                var adapterDirectory = rtx.Config.Get<string>("adapter.customAdapterDirectory");
                if (string.IsNullOrWhiteSpace(adapterDirectory))
                {
                    adapterDirectory = Path.Combine(AppContext.BaseDirectory, "Adapters", "custom");
                }
                else if (!Path.IsPathRooted(adapterDirectory))
                {
                    adapterDirectory = Path.Combine(AppContext.BaseDirectory, adapterDirectory);
                }
                var adapterAutoStart = rtx.Config.Get<bool>("adapter.adapterAutoStart");
                if (adapterAutoStart && !string.IsNullOrWhiteSpace(adapterDirectory))
                {
                    customAdapterRuntime.LoadAdapters(adapterDirectory);
                    await customAdapterRuntime.StartAllAsync();
                }

                rtx.Logger.LogInfo($"TCP-Bridge auf {bindHost}:{port} gestartet, Node.js-Adapter-Runtime initialisiert.");
            }

            rtx.IsRunning = true;
            rtx.Logger.LogInfo("System erfolgreich gestartet.");
        }

        void IOnStart.OnStart(IRuntimeContext rtx)
        {
            rtx.Logger.LogInfo("System wird gestartet (synchron)...");
            ConfigLoader.LoadIfExistsAsync(rtx).GetAwaiter().GetResult();

            if (rtx is MainRuntimeContext mainContext)
            {
                var snapshotPath = System.IO.Path.Combine(AppContext.BaseDirectory, "snapshot.jsonl");
                mainContext.InitializeMatrixAndBus(snapshotPath);
                mainContext.InitializeLocalization();
                mainContext.Matrix.LoadSnapshotAsync().GetAwaiter().GetResult();
                mainContext.Matrix.StartCronSnapshot(TimeSpan.FromMinutes(5));
                rtx.Logger.LogInfo("SemanticStateMatrix, SystemBus und AdapterRouter initialisiert.");

                var port = rtx.Config.Get<int>("ipc.ipcPort");
                if (port <= 0)
                {
                    port = 5001;
                }

                var bindHost = rtx.Config.Get<string>("ipc.ipcHost");
                if (string.IsNullOrWhiteSpace(bindHost))
                {
                    bindHost = "127.0.0.1";
                }

                var healthCheckIntervalMs = rtx.Config.Get<int>("adapter.adapterHealthCheckIntervalMs");
                if (healthCheckIntervalMs <= 0)
                {
                    healthCheckIntervalMs = 5000;
                }

                var crashRestartDelayMs = rtx.Config.Get<int>("adapter.adapterCrashRestartDelayMs");
                if (crashRestartDelayMs < 0)
                {
                    crashRestartDelayMs = 2000;
                }

                mainContext.StartTcpBridgeAsync(port, bindHost).GetAwaiter().GetResult();
                mainContext.InitializeNodeRuntime(
                    TimeSpan.FromMilliseconds(crashRestartDelayMs),
                    TimeSpan.FromMilliseconds(healthCheckIntervalMs));

                var adapterRootPath = rtx.Config.Get<string>("adapter.adapterRootPath");
                if (string.IsNullOrWhiteSpace(adapterRootPath))
                {
                    adapterRootPath = "Adapters";
                }

                if (!Path.IsPathRooted(adapterRootPath))
                {
                    adapterRootPath = Path.Combine(AppContext.BaseDirectory, adapterRootPath);
                }

                mainContext.InitializeAdapterDeployment(adapterRootPath);
                mainContext.InitializeLegacyAdapters(bindHost, port);
                mainContext.InitializeBackupService();
                mainContext.InitializeSecurityAsync().GetAwaiter().GetResult();
                mainContext.InitializeCustomAdapterRuntime();
                mainContext.LegacyAdapters?.StartAllAsync(rtx.Config.Get<bool>("adapter.legacyAutoStart")).GetAwaiter().GetResult();
                var customAdapterRuntime = mainContext.CustomAdapterRuntime!;
                var adapterDirectory = rtx.Config.Get<string>("adapter.customAdapterDirectory");
                if (string.IsNullOrWhiteSpace(adapterDirectory))
                {
                    adapterDirectory = Path.Combine(AppContext.BaseDirectory, "Adapters", "custom");
                }
                else if (!Path.IsPathRooted(adapterDirectory))
                {
                    adapterDirectory = Path.Combine(AppContext.BaseDirectory, adapterDirectory);
                }
                var adapterAutoStart = rtx.Config.Get<bool>("adapter.adapterAutoStart");
                if (adapterAutoStart && !string.IsNullOrWhiteSpace(adapterDirectory))
                {
                    customAdapterRuntime.LoadAdapters(adapterDirectory);
                    customAdapterRuntime.StartAllAsync().GetAwaiter().GetResult();
                }

                rtx.Logger.LogInfo($"TCP-Bridge auf {bindHost}:{port} gestartet, Node.js-Adapter-Runtime initialisiert.");
            }

            rtx.IsRunning = true;
            rtx.Logger.LogInfo("System erfolgreich gestartet.");
        }
    }
}
