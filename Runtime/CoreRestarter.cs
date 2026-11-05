using System.Diagnostics;
using ioBroker_NewGen.Controllers;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Runtime
{
    /// <summary>
    /// Leitet nach einem vollständig abgeschlossenen OnStop den Neustart ein. Es wird nie ein Prozess hart beendet;
    /// der alte Prozess endet regulär, nachdem er den Neustart angestoßen hat.
    /// </summary>
    internal static class CoreRestarter
    {
        /// <summary>Exitcode, den ein Supervisor (z. B. systemd <c>RestartForceExitStatus=75</c>) als Neustartwunsch erkennt.</summary>
        public const int RestartExitCode = 75;

        /// <summary>Liefert den Exitcode des Prozesses.</summary>
        public static int Restart(IRuntimeContext context)
        {
            var command = ReadRestartCommand(context);
            try
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    context.Logger.LogInfo($"[Restart] Starte konfigurierten Neustart-Befehl: {command}");
                    StartShell(command);
                    return 0;
                }

                // Unter systemd würde ein Kindprozess beim Stoppen der Unit beendet; der Supervisor startet selbst neu.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
                {
                    context.Logger.LogInfo($"[Restart] Beende mit Exitcode {RestartExitCode}; der Supervisor (Restart=on-failure/always) startet den Core neu.");
                    return RestartExitCode;
                }

                context.Logger.LogInfo("[Restart] Starte den Core als neuen Prozess.");
                StartSelf();
                return 0;
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"[Restart] Neustart fehlgeschlagen: {ex.Message}");
                return 1;
            }
        }

        private static string? ReadRestartCommand(IRuntimeContext context)
        {
            try
            {
                return context.Config.Get<string>("node.restartCommand");
            }
            catch
            {
                return null;
            }
        }

        private static void StartShell(string command)
        {
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
            info.UseShellExecute = false;
            info.WorkingDirectory = AppContext.BaseDirectory;
            Process.Start(info)?.Dispose();
        }

        private static void StartSelf()
        {
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Prozesspfad unbekannt.");
            var info = new ProcessStartInfo(processPath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("Einstiegs-Assembly unbekannt."));
            }

            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info)?.Dispose();
        }
    }
}
