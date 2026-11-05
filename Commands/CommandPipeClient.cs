using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ioBroker_NewGen.Commands
{
    /// <summary>
    /// Übersetzt Kommandozeilenargumente (<c>-stop alexa.0</c>, <c>-unload alexa</c>, <c>-cmd &lt;name&gt; …</c>,
    /// <c>-&lt;name&gt; …</c>) in einen Command und schickt ihn an den laufenden Core. Unabhängig vom
    /// Host-Prozess (exe, Linux-ELF, dotnet-DLL): es werden nur Argumente und ein lokaler Pipe-Name benötigt.
    /// </summary>
    public static class CommandPipeClient
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitNotRunning = 2;

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stop"] = "adapter-lifecycle",
            ["unload"] = "adapter-lifecycle"
        };

        private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["shutdown"] = CoreLifecycleControl.ShutdownName,
            ["restart"] = CoreLifecycleControl.RestartName
        };

        /// <summary>Liefert <c>false</c>, wenn die Argumente keinen Command darstellen (normaler Core-Start).</summary>
        public static bool TryParseArguments(string[]? args, out string[] tokens)
        {
            tokens = Array.Empty<string>();
            if (args is null || args.Length == 0 || !args[0].StartsWith('-'))
            {
                return false;
            }

            var name = args[0].TrimStart('-');
            var rest = args.Skip(1).ToArray();
            if (name.Length == 0)
            {
                return false;
            }

            if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                if (rest.Length == 0)
                {
                    return false;
                }

                tokens = rest;
                return true;
            }

            tokens = Aliases.TryGetValue(name, out var target)
                ? new[] { target, name.ToLowerInvariant() }.Concat(rest).ToArray()
                : new[] { Renames.GetValueOrDefault(name, name) }.Concat(rest).ToArray();
            return true;
        }

        /// <summary>Sendet den Command an den Core und gibt einen Prozess-Exitcode zurück.</summary>
        public static async Task<int> RunAsync(string[] tokens, TextWriter? output = null, TextWriter? error = null, TimeSpan? connectTimeout = null)
        {
            output ??= Console.Out;
            error ??= Console.Error;
            using var pipe = new NamedPipeClientStream(
                ".",
                CommandPipeServer.GetPipeName(),
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)(connectTimeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync("Kein laufender ioBroker-NewGen-Core erreichbar (oder Zugriff verweigert).");
                return ExitNotRunning;
            }

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(tokens));

            var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var response = await reader.ReadLineAsync();
            if (response is null)
            {
                await error.WriteLineAsync("Keine Antwort vom Core.");
                return ExitFailed;
            }

            if (response.StartsWith("OK", StringComparison.Ordinal))
            {
                await output.WriteLineAsync(response[2..].TrimStart());
                return ExitOk;
            }

            await error.WriteLineAsync(response.StartsWith("ERR", StringComparison.Ordinal) ? response[3..].TrimStart() : response);
            return ExitFailed;
        }
    }
}
