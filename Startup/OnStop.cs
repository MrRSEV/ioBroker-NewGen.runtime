using RSEV.Utilities.Lifecycle;
using RSEV.Utilities.Logging;
using RSEV.Utilities.Runtime;
using ioBroker_NewGen.Controllers;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Wird beim geordneten Herunterfahren des Systems ausgef�hrt.
    /// </summary>
    public class OnStop : IOnStop
    {
        public async Task OnStopAsync(IRuntimeContext context)
        {
            context.Logger.LogInfo("System wird heruntergefahren...");

            if (context is MainRuntimeContext mainContext)
            {
                await mainContext.ShutdownBridgeAndNodeRuntimeAsync();
                context.Logger.LogInfo("TCP-Bridge und Node.js-Adapter-Runtime gestoppt.");

                if (mainContext.Matrix is not null)
                {
                    mainContext.Matrix.StopCronSnapshot();
                    await mainContext.Matrix.WriteSnapshotAsync();
                    context.Logger.LogInfo("Snapshot der SemanticStateMatrix geschrieben.");
                }

                if (mainContext.BackupService is not null)
                {
                    var backupDirectory = Path.Combine(AppContext.BaseDirectory, "backups");
                    var backupPath = await mainContext.BackupService.CreateBackupAsync(backupDirectory);
                    context.Logger.LogInfo($"Backup erstellt: {backupPath}");
                }

                mainContext.ShutdownInfrastructure();
            }

            context.IsRunning = false;
            context.Logger.LogInfo("System erfolgreich heruntergefahren.");
        }

        void IOnStop.OnStop(IRuntimeContext context)
        {
            context.Logger.LogInfo("System wird heruntergefahren (synchron)...");

            if (context is MainRuntimeContext mainContext)
            {
                mainContext.ShutdownBridgeAndNodeRuntimeAsync().GetAwaiter().GetResult();
                context.Logger.LogInfo("TCP-Bridge und Node.js-Adapter-Runtime gestoppt.");

                if (mainContext.Matrix is not null)
                {
                    mainContext.Matrix.StopCronSnapshot();
                    mainContext.Matrix.WriteSnapshotAsync().GetAwaiter().GetResult();
                    context.Logger.LogInfo("Snapshot der SemanticStateMatrix geschrieben.");
                }

                if (mainContext.BackupService is not null)
                {
                    var backupDirectory = Path.Combine(AppContext.BaseDirectory, "backups");
                    var backupPath = mainContext.BackupService.CreateBackupAsync(backupDirectory).GetAwaiter().GetResult();
                    context.Logger.LogInfo($"Backup erstellt: {backupPath}");
                }

                mainContext.ShutdownInfrastructure();
            }

            context.IsRunning = false;
            context.Logger.LogInfo("System erfolgreich heruntergefahren.");
        }
    }
}
