using ioBroker_NewGen.Controllers;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using RSEV.Utilities.Commands;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Commands
{
    /// <summary>
    /// Stoppt native (Custom) und Legacy-/Node-Adapter zur Laufzeit und nimmt sie optional aus der
    /// Registry des RuntimeContext. Damit lassen sich Adapter-DLLs oder Node-Adapter im Betrieb austauschen.
    /// Aufruf: <c>adapter-lifecycle &lt;stop|unload&gt; &lt;adapterId|adapterName&gt;</c>.
    /// Ein Name betrifft alle Instanzen dieses Adapters.
    /// </summary>
    public sealed class AdapterLifecycleControl : BaseCommand
    {
        public const string StopAction = "stop";
        public const string UnloadAction = "unload";

        public AdapterLifecycleControl()
            : base("adapter-lifecycle", "Stoppt einen Adapter (stop) oder stoppt ihn und entfernt ihn aus der Registry (unload): adapter-lifecycle <stop|unload> <id|name>")
        {
        }

        public override async Task ExecuteAsync(IRuntimeContext context, params string[] arguments)
        {
            var runtime = context as MainRuntimeContext ?? MainRuntimeContext.Instance;
            if (arguments is null || arguments.Length < 2)
            {
                runtime.Logger.LogWarning($"[AdapterLifecycle] Verwendung: {Name} <{StopAction}|{UnloadAction}> <id|name>");
                return;
            }

            var action = arguments[0].Trim().ToLowerInvariant();
            if (action != StopAction && action != UnloadAction)
            {
                runtime.Logger.LogWarning($"[AdapterLifecycle] Unbekannte Aktion '{arguments[0]}'.");
                return;
            }

            var registry = runtime.AdapterRegistry;
            if (registry is null)
            {
                runtime.Logger.LogWarning("[AdapterLifecycle] Adapter-Registry nicht initialisiert.");
                return;
            }

            var reference = arguments[1].Trim();
            var targets = registry.Get(reference) is { } exact
                ? new[] { exact }
                : registry.GetAll()
                    .Where(a => string.Equals(a.Name, reference, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(a => a.Id, StringComparer.Ordinal)
                    .ToArray();

            if (targets.Length == 0)
            {
                runtime.Logger.LogWarning($"[AdapterLifecycle] Adapter '{reference}' ist nicht registriert.");
                return;
            }

            foreach (var target in targets)
            {
                var ok = await ApplyAsync(runtime, target, action == UnloadAction);
                runtime.Logger.LogInfo($"[AdapterLifecycle] {action} '{target.Id}': {(ok ? "erfolgreich" : "fehlgeschlagen")}");
            }
        }

        private static async Task<bool> ApplyAsync(MainRuntimeContext runtime, AdapterRegistration target, bool unload)
        {
            try
            {
                if (target.Type == AdapterType.Custom)
                {
                    var custom = runtime.CustomAdapterRuntime;
                    if (custom is null)
                    {
                        return false;
                    }

                    return unload
                        ? await custom.UnloadAdapterAsync(target.Id)
                        : await custom.StopAdapterAsync(target.Id);
                }

                // Legacy- und reine Node-Adapter laufen beide über die NodeRuntime.
                var legacy = runtime.LegacyAdapters;
                if (legacy is not null && legacy.IsInstance(target.Id))
                {
                    return unload
                        ? await legacy.DetachInstanceAsync(target.Id)
                        : await legacy.StopInstanceAsync(target.Id);
                }

                var node = runtime.NodeRuntime;
                if (node is null)
                {
                    return false;
                }

                var stopped = await node.StopAdapterAsync(target.Id);
                if (unload)
                {
                    runtime.AdapterRegistry.Unregister(target.Id);
                    return true;
                }

                return stopped;
            }
            catch (Exception ex)
            {
                runtime.Logger.LogError($"[AdapterLifecycle] '{target.Id}': {ex.Message}");
                return false;
            }
        }
    }
}
