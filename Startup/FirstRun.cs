using RSEV.Utilities.Lifecycle;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Wird einmalig beim allerersten Start des Systems ausgeführt,
    /// z. B. um Standard-Konfiguration oder Verzeichnisse anzulegen.
    /// </summary>
    internal class FirstRun : IFirstRun
    {
        public void OnFirstRun(IRuntimeContext rtx)
        {
            rtx.Logger.LogInfo("Erststart erkannt – Grundkonfiguration wird angelegt.");
            // Erstelle die Standard-Konfiguration (config.toml)
            // und andere initiale Artefakte in einer separaten Klasse.
            _ = ConfigCreator.CreateDefaultConfigAsync(rtx);
        }

        public Task OnFirstRunAsync(IRuntimeContext rtx)
        {
            // Asynchrone Initialisierung: delegieren an ConfigCreator.
            return ConfigCreator.CreateDefaultConfigAsync(rtx);
        }

        public void OnStart(IRuntimeContext rtx)
        {
            rtx.Logger.LogInfo("FirstRun.OnStart wird ausgeführt.");
        }

        public Task OnStartAsync(IRuntimeContext rtx)
        {
            OnStart(rtx);
            return Task.CompletedTask;
        }
    }
}
