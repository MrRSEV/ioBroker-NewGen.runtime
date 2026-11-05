using ioBroker_NewGen.Controllers;
using ioBroker_NewGen.Runtime;
using ioBroker_NewGen.Startup;

public static class MainClass
{
    static async Task<int> Main(string[] args)
    {
        // Command-Modus: Befehl an den bereits laufenden Core senden, keinen zweiten Core starten.
        if (ioBroker_NewGen.Commands.CommandPipeClient.TryParseArguments(args, out var commandTokens))
        {
            return await ioBroker_NewGen.Commands.CommandPipeClient.RunAsync(commandTokens);
        }

        var rtx = MainRuntimeContext.Instance;

        var isFirstRun = !File.Exists(ConfigCreator.ConfigPath);
        if (isFirstRun)
        {
            await new FirstRun().OnFirstRunAsync(rtx);
        }

        await new OnStart().OnStartAsync(rtx);

        UnexpectedUserExitHandler.Initialize(rtx);
        return await UnexpectedUserExitHandler.RunCommandLoopAsync();
    }
}
