using RSEV.Utilities.Logging;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.Runtime;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Bridge;
using ioBroker_NewGen.Core.Adapters;
using ioBroker_NewGen.Core.Events;
using ioBroker_NewGen.Core.Deployment;
using ioBroker_NewGen.Core.Backup;
using RSEV.Utilities.Events;

namespace ioBroker_NewGen.Controllers
{
    /// <summary>
    /// Konkreter, applikationsweiter RuntimeContext.
    /// Wird als Singleton verwendet, damit alle Lifecycle-Hooks
    /// (IFirstRun, IOnStart, IOnStop) auf denselben Logger, dieselbe
    /// Config und denselben globalen Zustand zugreifen.
    /// </summary>
    public sealed class MainRuntimeContext : BaseRuntimeContext
    {
        private static readonly Lazy<MainRuntimeContext> _instance = new(() => new MainRuntimeContext());

        /// <summary>
        /// Die einzige Instanz des MainRuntimeContext f�r den gesamten Prozess.
        /// </summary>
        public static MainRuntimeContext Instance => _instance.Value;

        /// <summary>
        /// Zentrale semantische Zustands- und Referenzmatrix des Systems
        /// (Objekte, States, History, semantischer Index, Snapshot-Persistenz).
        /// Wird beim Systemstart (<see cref="ioBroker_NewGen.Startup.OnStart"/>) initialisiert.
        /// </summary>
        public SemanticStateMatrix Matrix { get; private set; } = null!;

        /// <summary>
        /// Zentraler, neutraler Datenbus des Systems (BusFrame-Transport).
        /// </summary>
        public SystemMessageBus SystemBus { get; private set; } = null!;

        /// <summary>
        /// RSEV.Utilities-basierter EventBus für State- und Adapterereignisse.
        /// </summary>
        public SystemEventBus SystemEventBus { get; private set; } = null!;

        /// <summary>
        /// Entscheidet, ob Frames intern (Custom-Adapter) oder �ber die
        /// TCP-Bridge (ioBroker-Legacy-Adapter) geroutet werden.
        /// </summary>
        public AdapterRouter AdapterRouter { get; private set; } = null!;

        /// <summary>
        /// Zentrale Registry aller bekannten Adapter (Custom + ioBroker-Legacy)
        /// inkl. Status, TCP-Endpunkt bzw. Instanzreferenz.
        /// </summary>
        public AdapterRegistry AdapterRegistry { get; private set; } = null!;

        /// <summary>
        /// TCP-Bridge-Server für isolierte Legacy-ioBroker-Adapter (Node.js-Prozesse).
        /// </summary>
        public TcpBridgeServer? TcpBridge { get; private set; }

        /// <summary>
        /// Verwaltet isolierte Node.js-Adapterprozesse (Start, Crash-Recovery, Heartbeat).
        /// </summary>
        public NodeAdapterRuntime? NodeRuntime { get; private set; }

        /// <summary>Installation und Instanzen der Legacy-Adapter (ZIP-Upload, Lifecycle).</summary>
        public LegacyAdapterManager? LegacyAdapters { get; private set; }

        /// <summary>
        /// Runtime für host-interne C#-Adapter.
        /// </summary>
        public CustomAdapterRuntime? CustomAdapterRuntime { get; private set; }

        public AdapterInstaller? AdapterInstaller { get; private set; }

        public AdapterPackageInstaller? AdapterPackageInstaller { get; private set; }

        public AdapterRepositoryClient? AdapterRepositoryClient { get; private set; }

        public BackupService? BackupService { get; private set; }

        /// <summary>
        /// Eingebettete Sprachweiche (Sprache aus [localization]); von Matrix, AdminUI und Adaptern genutzt.
        /// </summary>
        public ioBroker_NewGen.Core.Localization.ILocalizationService Localization { get; private set; } =
            ioBroker_NewGen.Core.Localization.LocalizationService.Create(
                overrideDirectory: Path.Combine(AppContext.BaseDirectory, "Localization"));

        /// <summary>
        /// Liest [localization] aus der Config, setzt Sprache und Rückfallsprache und spiegelt sie in die Matrix.
        /// </summary>
        public void InitializeLocalization()
        {
            var fallback = Config.Get<string>("localization.fallbackLanguage");
            var language = Config.Get<string>("localization.language");
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                Localization.SetFallbackLanguage(fallback);
            }

            language = string.IsNullOrWhiteSpace(language) ? "de_de" : language;
            if (!Localization.SetLanguage(language))
            {
                Console.WriteLine($"[Localization] Sprache '{language}' unbekannt, verwende {Localization.Language}.");
            }

            if (Matrix is not null)
            {
                Matrix.Localization = Localization;
                const string stateId = "system.localization.language";
                Matrix.RegisterObject(new MatrixObject
                {
                    Id = stateId,
                    Name = "Active language",
                    Type = "state",
                    Meta = new Dictionary<string, string> { ["role"] = "language", ["semantic"] = "localization.language" }
                });
                Matrix.SetState(stateId, Localization.Language);
                Localization.LanguageChanged += code => Matrix.SetState(stateId, code);
            }
        }

        /// <summary>
        /// Benutzer-, Rollen- und Sitzungsverwaltung (SQLite, data/security.db).
        /// </summary>
        public ioBroker.Core.Security.SecurityService? Security { get; private set; }

        /// <summary>
        /// Initialisiert das Security-Modul und spiegelt unkritische Zustände in die Matrix.
        /// Erwartet, dass <see cref="InitializeMatrixAndBus"/> zuvor aufgerufen wurde.
        /// </summary>
        public async Task InitializeSecurityAsync(ioBroker.Core.Security.SecurityOptions? options = null)
        {
            if (Security is not null)
            {
                return;
            }

            options ??= new ioBroker.Core.Security.SecurityOptions();
            if (!Path.IsPathRooted(options.DatabasePath))
            {
                options.DatabasePath = Path.Combine(AppContext.BaseDirectory, options.DatabasePath);
            }

            var service = new ioBroker.Core.Security.SecurityService(
                options,
                new ioBroker.Core.Security.SystemEventBusSecurityPublisher(SystemEventBus));

            if (options.MatrixMirrorEnabled)
            {
                service.AttachMirror(
                    new ioBroker.Core.Security.SecurityMatrixMirror(Matrix, service.Users, service.Sessions),
                    SystemEventBus);
            }

            var bootstrapCreated = await service.InitializeAsync();
            Security = service;

            if (service.Mirror is not null)
            {
                await service.Mirror.RefreshActiveSessionsAsync();
                service.Mirror.WriteFailedLogins(0);
            }

            if (bootstrapCreated)
            {
                Logger.LogWarning($"[Security] Standardadministrator '{options.DefaultUsername}' angelegt. Das Passwort muss beim ersten Login geändert werden.");
            }
        }

        private bool _matrixBusWired;
        private AdapterLifecycleLogHandler? _adapterLifecycleLogHandler;

        private MainRuntimeContext()
        {
        }

        /// <summary>
        /// Initialisiert Matrix, Systembus und Router. Muss einmalig beim
        /// Systemstart aufgerufen werden, bevor diese Properties verwendet werden.
        /// </summary>
        public void InitializeMatrixAndBus(string snapshotPath)
        {
            if (SystemBus is null)
            {
                SystemBus = new SystemMessageBus();
                MessageBusRegistry.Register(SystemBus);
                SystemBus.Initialize(this);
            }

            if (SystemEventBus is null)
            {
                SystemEventBus = new SystemEventBus();
                MessageBusRegistry.Register(SystemEventBus);
                SystemEventBus.Initialize(this);
            }

            if (_adapterLifecycleLogHandler is null)
            {
                _adapterLifecycleLogHandler = new AdapterLifecycleLogHandler(Logger);
                SystemEventBus.AdapterLifecycle.Subscribe(_adapterLifecycleLogHandler);
            }

            var roleStorePath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(snapshotPath)) ?? AppContext.BaseDirectory,
                "roles.json");
            Matrix ??= new SemanticStateMatrix(
                snapshotPath,
                roleService: new DefaultRoleService(roleStorePath),
                eventBus: SystemEventBus);
            Matrix.Localization = Localization;

            AdapterRouter ??= new AdapterRouter(SystemBus, Logger);
            AdapterRegistry ??= new AdapterRegistry(SystemEventBus);

            if (!_matrixBusWired)
            {
                // Sprint-1-Datenpfad: StateUpdate-Frames werden über den
                // RSEV.Utilities-basierten SystemBus in die Matrix übernommen.
                SystemBus.Subscribe("*", frame => Matrix.ApplyFrame(frame));
                _matrixBusWired = true;
            }
        }

        /// <summary>
        /// Startet die TCP-Bridge (Sprint 2) auf dem übergebenen Port und
        /// verknüpft sie mit dem <see cref="AdapterRouter"/>, sodass Frames
        /// für <see cref="AdapterType.IoBroker"/> tatsächlich zugestellt werden.
        /// Erwartet, dass <see cref="InitializeMatrixAndBus"/> zuvor aufgerufen wurde.
        /// </summary>
        public async Task StartTcpBridgeAsync(int port, string bindHost = "127.0.0.1")
        {
            if (TcpBridge is not null)
            {
                return;
            }

            TcpBridge = new TcpBridgeServer(port, bindHost, AdapterRegistry, SystemBus, Logger);
            await TcpBridge.StartAsync();
            AdapterRouter.AttachTcpBridge(TcpBridge);
        }

        /// <summary>
        /// Initialisiert die Node.js-Adapter-Runtime (Sprint 2) auf Basis von
        /// <see cref="RSEV.Utilities.Processes.RuntimeProcessController"/> (aus <see cref="ProcessController"/>)
        /// und startet die periodische Health-Überwachung/Crash-Recovery.
        /// </summary>
        public void InitializeNodeRuntime(
            TimeSpan? crashRestartDelay = null,
            TimeSpan? healthCheckInterval = null)
        {
            NodeRuntime ??= new NodeAdapterRuntime(
                ProcessController,
                AdapterRegistry,
                Logger,
                crashRestartDelay,
                healthCheckInterval);
            NodeRuntime.StartHealthMonitoring();
        }

        /// <summary>
        /// Initialisiert die CustomAdapter-Runtime auf Basis des RSEV.Utilities-
        /// AssemblyLoaders.
        /// </summary>
        public void InitializeCustomAdapterRuntime()
        {
            CustomAdapterRuntime ??= new CustomAdapterRuntime(
                AssemblyLoader,
                Matrix,
                SystemBus,
                AdapterRouter,
                AdapterRegistry,
                Logger,
                this,
                AdapterInstaller,
                AdapterPackageInstaller,
                AdapterRepositoryClient,
                LegacyAdapters);
        }

        public void InitializeLegacyAdapters(string bridgeHost, int bridgePort)
        {
            if (NodeRuntime is null || AdapterInstaller is null)
            {
                return;
            }

            LegacyAdapters ??= new LegacyAdapterManager(
                NodeRuntime,
                AdapterInstaller,
                AdapterRegistry,
                Config,
                Logger,
                bridgeHost,
                bridgePort);
        }

        public void InitializeAdapterDeployment(string rootPath)
        {
            AdapterInstaller ??= new AdapterInstaller(rootPath);
            AdapterInstaller.EnsureLayout();
            AdapterPackageInstaller ??= new AdapterPackageInstaller(AdapterInstaller);

            var requireSignature = false;
            string? trustedPublicKeyPem = null;
            try
            {
                requireSignature = Config.Get<bool>("adapter.requireSignedPackages");
                trustedPublicKeyPem = Config.Get<string>("adapter.trustedPackagePublicKeyPem");
            }
            catch
            {
                // Ältere Konfigurationen behalten den kompatiblen optionalen Signaturmodus.
            }

            AdapterRepositoryClient ??= new AdapterRepositoryClient(
                AdapterPackageInstaller,
                securityOptions: new AdapterRepositorySecurityOptions
                {
                    RequireSignature = requireSignature,
                    TrustedPublicKeyPem = trustedPublicKeyPem
                });
        }

        public void InitializeBackupService()
        {
            BackupService ??= new BackupService(AppContext.BaseDirectory);
        }

        /// <summary>
        /// Stoppt TCP-Bridge und Node.js-Adapter-Runtime (z. B. beim Systemstopp).
        /// </summary>
        public async Task ShutdownBridgeAndNodeRuntimeAsync()
        {
            if (CustomAdapterRuntime is not null)
            {
                await CustomAdapterRuntime.DisposeAsync();
            }

            if (NodeRuntime is not null)
            {
                await NodeRuntime.StopAllAsync();
            }

            if (TcpBridge is not null)
            {
                await TcpBridge.StopAsync();
            }
        }

        public void ShutdownInfrastructure()
        {
            if (SystemEventBus is not null)
            {
                SystemEventBus.Shutdown(this);
            }

            if (SystemBus is not null)
            {
                SystemBus.Shutdown(this);
            }
        }

        /// <summary>
        /// Erstellt das Konfigurationsregister des RSEV.Utilities-Frameworks.
        /// Die Instanz wird von BaseRuntimeContext während der Initialisierung
        /// dieses RuntimeContext registriert.
        /// </summary>
        protected override IConfig CreateDefaultConfig()
        {
            return new ConfConfig();
        }

        /// <summary>
        /// Verwendet einen sprechenden Logger-Namen statt des Standardnamens.
        /// </summary>
        protected override ILogger CreateLogger()
        {
            var logger = new SystemLog(Path.Combine(AppContext.BaseDirectory, "log"));
            logger.Init();
            return logger;
        }

        private sealed class AdapterLifecycleLogHandler : BaseEventHandler<AdapterLifecycleEventArgs>
        {
            private readonly ILogger _logger;

            public AdapterLifecycleLogHandler(ILogger logger)
            {
                _logger = logger;
            }

            protected override void OnHandle(object sender, AdapterLifecycleEventArgs args)
            {
                var adapter = args.Registration;
                var description = $"Adapter '{adapter.Name}' (ID: {adapter.Id}, Typ: {adapter.Type})";

                switch (adapter.Status)
                {
                    case AdapterStatus.Connected:
                        _logger.LogInfo($"{description} gestartet.");
                        break;
                    case AdapterStatus.Disconnected when args.PreviousStatus == AdapterStatus.Connected:
                        _logger.LogInfo($"{description} gestoppt.");
                        break;
                    case AdapterStatus.Faulted:
                        _logger.LogError($"{description} ist in einen Fehlerzustand gewechselt.");
                        break;
                }
            }
        }
    }
}
