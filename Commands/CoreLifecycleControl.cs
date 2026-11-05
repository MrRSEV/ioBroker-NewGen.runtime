using ioBroker_NewGen.Runtime;
using RSEV.Utilities.Commands;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Commands
{
    /// <summary>
    /// Beendet (<c>core-shutdown</c>) oder startet (<c>core-restart</c>) den Core immer über
    /// <c>OnStop.OnStopAsync()</c>, das auch alle Adapter und deren Prozesse stoppt – nie per Prozess-Kill.
    /// </summary>
    public sealed class CoreLifecycleControl : BaseCommand
    {
        public const string ShutdownName = "core-shutdown";
        public const string RestartName = "core-restart";

        private static readonly TimeSpan ResponseGrace = TimeSpan.FromMilliseconds(300);
        private readonly bool _restart;

        private CoreLifecycleControl(string name, string description, bool restart) : base(name, description)
        {
            _restart = restart;
        }

        public static CoreLifecycleControl Shutdown() =>
            new(ShutdownName, "Beendet den Core geordnet über OnStopAsync (stoppt alle Adapter).", restart: false);

        public static CoreLifecycleControl Restart() =>
            new(RestartName, "Startet den Core geordnet neu: OnStopAsync, danach Neustart (node.restartCommand, systemd oder Selbststart).", restart: true);

        public override Task ExecuteAsync(IRuntimeContext context, params string[] arguments)
        {
            // Kurze Verzögerung, damit CLI/Pipe ihre Antwort noch erhalten, bevor die Pipe heruntergefahren wird.
            if (UnexpectedUserExitHandler.RequestShutdown(context, _restart, ResponseGrace))
            {
                context.Logger.LogInfo(_restart
                    ? "[CoreLifecycle] Neustart eingeleitet (OnStopAsync, danach Neustart)."
                    : "[CoreLifecycle] Shutdown eingeleitet (OnStopAsync).");
            }
            else
            {
                context.Logger.LogWarning("[CoreLifecycle] Es läuft bereits ein Shutdown.");
            }

            return Task.CompletedTask;
        }
    }
}
