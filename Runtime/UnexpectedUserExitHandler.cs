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
        private static int _stopInitiated;
        private static volatile bool _stopSucceeded;
        private static readonly TaskCompletionSource _shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True, wenn der laufende Shutdown ein Neustart des Cores sein soll.</summary>
        public static bool RestartRequested { get; private set; }

        private static bool TryBeginStop() => Interlocked.Exchange(ref _stopInitiated, 1) == 0;

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

            // Unter Linux/macOS beendet z. B. 'systemctl stop' den Prozess per SIGTERM; auch dann muss OnStop laufen.
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                context.Logger.LogWarning("[ExitHandler] SIGTERM erkannt – Shutdown wird eingeleitet...");
                ExecuteOnStopSync(context);
            }));
        }

        private static readonly List<PosixSignalRegistration> _signalRegistrations = new();

        /// <summary>
        /// Fordert den geordneten Shutdown über <see cref="OnStop.OnStopAsync"/> an (nie per Prozess-Kill).
        /// Kehrt sofort zurück, der Shutdown läuft im Hintergrund, damit z. B. ein Pipe-Command noch antworten kann.
        /// </summary>
        /// <returns><c>false</c>, wenn bereits ein Shutdown läuft.</returns>
        public static bool RequestShutdown(IRuntimeContext context, bool restart, TimeSpan? delay = null)
        {
            if (!TryBeginStop())
            {
                return false;
            }

            RestartRequested = restart;
            _ = Task.Run(async () =>
            {
                if (delay is { } wait)
                {
                    await Task.Delay(wait);
                }

                await RunOnStopAsync(context);
            });
            return true;
        }

        /// <summary>
        /// Liest Konsolenbefehle und wartet, bis der Shutdown abgeschlossen ist. Liefert den Prozess-Exitcode;
        /// bei einem Neustart wird dieser nach vollständigem OnStop über <see cref="CoreRestarter"/> eingeleitet.
        /// </summary>
        public static async Task<int> RunCommandLoopAsync()
        {
            var rtx = MainRuntimeContext.Instance;
            _ = Task.Run(() => ReadConsoleAsync(rtx));

            await _shutdownCompleted.Task;

            if (!RestartRequested)
            {
                return 0;
            }

            if (!_stopSucceeded)
            {
                rtx.Logger.LogError("[ExitHandler] Neustart abgebrochen: OnStop wurde nicht sauber abgeschlossen.");
                return 1;
            }

            return CoreRestarter.Restart(rtx);
        }

        private static async Task ReadConsoleAsync(MainRuntimeContext rtx)
        {
            while (!_shutdownCompleted.Task.IsCompleted)
            {
                var input = await Task.Run(Console.ReadLine);
                if (input is null)
                {
                    // Keine Konsole (z. B. Dienstbetrieb): Steuerung läuft über Signale und die Command-Pipe.
                    return;
                }

                if (string.IsNullOrWhiteSpace(input)) continue;

                if (string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
                {
                    rtx.Logger.LogInfo("Befehl 'exit' empfangen – Shutdown wird eingeleitet...");
                    RequestShutdown(rtx, restart: false);
                }
                else
                {
                    var result = await rtx.Commands.ExecuteLineAsync(input);
                    if (result.Success)
                    {
                        rtx.Logger.LogInfo(result.Message);
                    }
                    else
                    {
                        rtx.Logger.LogWarning(result.Message);
                    }
                }
            }
        }

        private static void ExecuteOnStopSync(IRuntimeContext context)
        {
            if (!TryBeginStop()) return;

            try
            {
                var shutdownTask = new OnStop().OnStopAsync(context);
                if (!shutdownTask.Wait(ShutdownTimeout))
                {
                    context.Logger.LogError($"[ExecuteOnStopSync] Shutdown überschreitet Timeout von {ShutdownTimeout.TotalSeconds} Sekunden.");
                }
                else
                {
                    _stopSucceeded = true;
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
                _shutdownCompleted.TrySetResult();
            }
        }

        private static async Task RunOnStopAsync(IRuntimeContext context)
        {
            try
            {
                using var cts = new CancellationTokenSource(ShutdownTimeout);
                var shutdownTask = new OnStop().OnStopAsync(context);

                if (await Task.WhenAny(shutdownTask, Task.Delay(Timeout.Infinite, cts.Token)) == shutdownTask)
                {
                    await shutdownTask;
                    _stopSucceeded = true;
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
                _shutdownCompleted.TrySetResult();
            }
        }
    }
}
