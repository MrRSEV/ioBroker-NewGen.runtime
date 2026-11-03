using RSEV.Utilities.Runtime;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Erstellt bei Erststart eine Standard-`config.toml`.
    /// Die Klasse h�lt die Logik getrennt von `FirstRun` und sorgt
    /// f�r eine gut strukturierte Initialisierung.
    /// </summary>
    internal static class ConfigCreator
    {
        internal static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.toml");

        public static async Task CreateDefaultConfigAsync(IRuntimeContext rtx)
        {
            rtx.Logger.LogInfo("Erstelle Standard-Konfiguration (config.toml)...");

            try
            {
                var configPath = ConfigPath;

                if (File.Exists(configPath))
                {
                    rtx.Logger.LogInfo($"Konfiguration existiert bereits: {configPath}");
                }
                else
                {
                    // Standard-TOML-Inhalt (Beispiel vom Benutzer) - als Zeilen zusammenbauen
                    var lines = new[]
                    {
                        "[node]",
                        "firstrun = false",
                        "nodeId = \"newgen-node-01\"",
                        "instanceName = \"NewGenRuntime\"",
                        "environment = \"prod\"",
                        "basePath = \"/var/newgen\"",
                        "tempPath = \"/var/newgen/tmp\"",
                        "maxParallelAdapters = 8",
                        "shutdownTimeoutSeconds = 10",
                        "runtimeMode = \"standalone\"",
                        "",
                        "[adapter]",
                        "adapterDirectory = \"/var/newgen/adapters\"",
                        "customAdapterDirectory = \"Adapters/custom\"",
                        "adapterRootPath = \"Adapters\"",
                        "enabledAdapters = [\"admin\"]",
                        "requireSignedPackages = false",
                        "trustedPackagePublicKeyPem = \"\"",
                        "legacyAutoStart = false",
                        "adapterAutoStart = true",
                        "adapterIsolationMode = \"process\"",
                        "adapterSandboxing = true",
                        "adapterMaxMemoryMB = 256",
                        "adapterMaxCpuPercent = 50",
                        "adapterStartupTimeoutMs = 3000",
                        "adapterCrashRestartDelayMs = 2000",
                        "adapterHealthCheckIntervalMs = 5000",
                        "adapterApiVersion = \"1.0\"",
                        "",
                        "[mqtt]",
                        "host = \"127.0.0.1\"",
                        "port = 1883",
                        "clientId = \"newgen-mqtt\"",
                        "username = \"\"",
                        "password = \"\"",
                        "useTls = false",
                        "keepAliveSeconds = 30",
                        "topicFilter = \"#\"",
                        "addressPrefix = \"mqtt\"",
                        "",
                        "[zigbee]",
                        "host = \"127.0.0.1\"",
                        "port = 1883",
                        "clientId = \"newgen-zigbee\"",
                        "username = \"\"",
                        "password = \"\"",
                        "useTls = false",
                        "keepAliveSeconds = 30",
                        "topicFilter = \"zigbee2mqtt/#\"",
                        "baseTopic = \"zigbee2mqtt\"",
                        "",
                        "[hue]",
                        "bridgeUri = \"http://127.0.0.1\"",
                        "apiKey = \"\"",
                        "pollIntervalMs = 10000",
                        "",
                        "[shelly]",
                        "deviceUri = \"http://127.0.0.1\"",
                        "switchId = 0",
                        "pollIntervalMs = 5000",
                        "",
                        "[state]",
                        "statePersistenceMode = \"sqlite\"",
                        "stateFilePath = \"/var/newgen/state.db\"",
                        "stateAutosaveIntervalMs = 5000",
                        "stateMaxHistoryEntries = 10000",
                        "stateSchemaVersion = \"1.0\"",
                        "stateCompression = true",
                        "",
                        "[ipc]",
                        "ipcMode = \"tcp\"",
                        "ipcHost = \"127.0.0.1\"",
                        "ipcPort = 5001",
                        "busQueueSize = 10000",
                        "busMaxFrameSize = 65536",
                        "busRetryIntervalMs = 200",
                        "busMaxSubscribers = 128",
                        "",
                        "[admin]",
                        "adminEnabled = true",
                        "adminPort = 8080",
                        "adminRequireAuthentication = true",
                        "adminToken = \"\"",
                        "",
                        "[backup]",
                        "backupEnabled = true",
                        "backupPath = \"backups\"",
                        "",
                        "[localization]",
                        "language = \"de_de\"",
                        "fallbackLanguage = \"en_GB\"",
                        "",
                        "[system]",
                        "setupDone = false",
                        "temperatureUnit = \"°C\"",
                        "currency = \"€\"",
                        "dateFormat = \"DD.MM.YYYY\"",
                        "isFloatComma = true",
                        "firstDayOfWeek = \"monday\"",
                        "country = \"\"",
                        "city = \"\"",
                        "address = \"\"",
                        "latitude = \"\"",
                        "longitude = \"\"",
                        "",
                        "[logging]",
                        "logLevel = \"info\"",
                        "logPath = \"/var/newgen/logs\"",
                        "logFileRotationMB = 10",
                        "logRetentionDays = 14",
                        "enableStructuredLogging = true",
                        "enableAdapterLogs = true",
                        "adapterLogPath = \"/var/newgen/logs/adapters\"",
                        "",
                        "[security]",
                        "allowRemoteControl = false",
                        "remoteControlToken = \"\"",
                        "adapterExecutionWhitelist = [\"ha\", \"mqtt\", \"zigbee\"]",
                        "adapterExecutionBlacklist = [\"debug\"]",
                        "sandboxFileAccessPolicy = \"restricted\"",
                        "sandboxNetworkPolicy = \"local-only\""
                    };

                    var toml = string.Join(Environment.NewLine, lines);

                    // Ensure directory exists
                    var dir = Path.GetDirectoryName(configPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    await File.WriteAllTextAsync(configPath, toml);
                    rtx.Logger.LogInfo($"Konfiguration angelegt: {configPath}");
                }

                await ConfigLoader.LoadIfExistsAsync(rtx);
            }
            catch (Exception ex)
            {
                rtx.Logger.LogError($"Fehler beim Anlegen der Konfiguration: {ex}");
                throw;
            }
        }
    }
}
