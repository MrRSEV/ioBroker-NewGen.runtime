using ioBroker_NewGen.Controllers;
using ioBroker_NewGen.Runtime;
using ioBroker_NewGen.Startup;

public static class MainClass
{
        static async Task Main(string[] args)
    {
        var rtx = MainRuntimeContext.Instance;

        var isFirstRun = !File.Exists(ConfigCreator.ConfigPath);
        if (isFirstRun)
        {
            await new FirstRun().OnFirstRunAsync(rtx);
        }

        await new OnStart().OnStartAsync(rtx);

        UnexpectedUserExitHandler.Initialize(rtx);
        await UnexpectedUserExitHandler.RunCommandLoopAsync();
    }
}
