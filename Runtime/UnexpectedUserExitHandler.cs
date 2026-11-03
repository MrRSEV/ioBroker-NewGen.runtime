using ioBroker_NewGen.Controllers;
using ioBroker_NewGen.Startup;
using RSEV.Utilities.Logging;
using RSEV.Utilities.Runtime;
using System.Runtime.InteropServices;

namespace ioBroker_NewGen.Runtime
{
    /// <summary>
    /// Behandelt geplante (CTRL+C, Konsole schließen) und ungeplante
    /// (Exceptions) Prozessenden und sorgt dafür, dass IOnStop.OnStopAsync
    /// zuverlässig aufgerufen wird, bevor der Prozess endet.
    /// </summary>
    internal static class UnexpectedUserExitHandler
    {
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
        private static bool _isRunning = true;
        private static bool _stopInitiated = false;

        // Native Windows Handler
        private delegate bool ConsoleCtrlDelegate(CtrlTypes ctrlType);

        private enum CtrlTypes
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        /// <summary>
        /// Registriert die Abbruch-Handler (CTRL+C, plattformübergreifend,
        /// sowie Windows-spezifische Fenster-/Logoff-/Shutdown-Events).
        /// </summary>
        public static void Initialize(IRuntimeContext context)
        {
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                context.Logger.LogWarning("[ExitHandler] CTRL+C erkannt – Shutdown wird eingeleitet...");
                ExecuteOnStopSync(context);
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SetConsoleCtrlHandler(ctrlType =>
                {
                    context.Logger.LogWarning($"[ExitHandler] Windows Exit erkannt ({ctrlType}) – Shutdown wird eingeleitet...");
                    ExecuteOnStopSync(context);
                    return true;
                }, true);
            }
        }

        /// <summary>
        /// Einfache Eingabeschleife, solange das System läuft.
        /// Kann später um echte Befehle erweitert werden.
        /// </summary>
        public static async Task RunCommandLoopAsync()
        {
            var rtx = MainRuntimeContext.Instance;

            while (_isRunning && rtx.IsRunning)
            {
                var input = await Task.Run(Console.ReadLine);
                if (string.IsNullOrWhiteSpace(input)) continue;

                if (string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    rtx.Logger.LogInfo("Befehl 'exit' empfangen – Shutdown wird eingeleitet...");
                    await ExecuteOnStopAsync(rtx);
                }
                else
                {
                    rtx.Logger.LogWarning($"Unbekannter Befehl: '{input}'");
                }
            }
        }

        private static void ExecuteOnStopSync(IRuntimeContext context)
        {
            if (_stopInitiated) return;
            _stopInitiated = true;

            try
            {
                var shutdownTask = new OnStop().OnStopAsync(context);
                if (!shutdownTask.Wait(ShutdownTimeout))
                {
                    context.Logger.LogError($"[ExecuteOnStopSync] Shutdown überschreitet Timeout von {ShutdownTimeout.TotalSeconds} Sekunden.");
                }
                else
                {
                    context.Logger.LogInfo("OnStop erfolgreich abgeschlossen (synchron).");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"[ExecuteOnStopSync] Fehler beim OnStop: {ex}");
                var summary = new UnexpectedExitHandler(context).Handle(ex);
                context.Logger.LogError(summary.ToString());
            }
            finally
            {
                _isRunning = false;
            }
        }

        private static async Task ExecuteOnStopAsync(IRuntimeContext context)
        {
            if (_stopInitiated) return;
            _stopInitiated = true;

            try
            {
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                var shutdownTask = new OnStop().OnStopAsync(context);

                if (await Task.WhenAny(shutdownTask, Task.Delay(Timeout.Infinite, cts.Token)) == shutdownTask)
                {
                    context.Logger.LogInfo("OnStop erfolgreich abgeschlossen (async).");
                }
                else
                {
                    context.Logger.LogError($"[ExecuteOnStopAsync] Shutdown überschreitet Timeout von {ShutdownTimeout.TotalSeconds} Sekunden.");
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Fehler beim OnStop: {ex}");
                var summary = await new UnexpectedExitHandler(context).HandleAsync(ex);
                context.Logger.LogError(summary.ToString());
            }
            finally
            {
                _isRunning = false;
            }
        }
    }
}
