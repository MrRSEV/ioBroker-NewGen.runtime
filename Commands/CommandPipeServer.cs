using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RSEV.Utilities.Logging;

namespace ioBroker_NewGen.Commands
{
    /// <summary>
    /// Lokaler Steuerkanal des laufenden Cores (Named Pipe; unter Linux/macOS ein Unix-Domain-Socket).
    /// Protokoll: eine JSON-Zeile (<c>string[]</c>: Command + Argumente) → eine Antwortzeile
    /// <c>OK &lt;text&gt;</c> oder <c>ERR &lt;text&gt;</c>. Zugriff nur für denselben Benutzer.
    /// </summary>
    public sealed class CommandPipeServer : IAsyncDisposable
    {
        private const int MaxFailures = 3;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly CoreCommandHost _host;
        private readonly ILogger _logger;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public CommandPipeServer(CoreCommandHost host, ILogger logger, string? baseDirectory = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pipeName = GetPipeName(baseDirectory);
        }

        /// <summary>Pro Installationsverzeichnis eindeutiger Pipe-Name, damit mehrere Installationen parallel laufen können.</summary>
        public static string GetPipeName(string? baseDirectory = null)
        {
            var dir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (OperatingSystem.IsWindows())
            {
                dir = dir.ToLowerInvariant();
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..12].ToLowerInvariant();
            return "ioBroker-NewGen-" + hash;
        }

        public void Start()
        {
            _loop ??= Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            var failures = 0;
            var announced = false;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    if (!announced)
                    {
                        announced = true;
                        _logger.LogInfo($"[CommandPipe] Steuerkanal '{_pipeName}' bereit.");
                    }

                    await pipe.WaitForConnectionAsync(token);
                    failures = 0;
                    await HandleAsync(pipe, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (++failures >= MaxFailures)
                    {
                        _logger.LogWarning($"[CommandPipe] Steuerkanal nicht verfügbar, wird deaktiviert: {ex.Message}");
                        return;
                    }

                    try
                    {
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);

            CommandResult result;
            try
            {
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                var tokens = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<string[]>(line);
                result = tokens is null || tokens.Length == 0
                    ? new CommandResult(false, "Ungültige Anfrage.")
                    : await _host.ExecuteAsync(tokens);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                result = new CommandResult(false, "Zeitüberschreitung beim Lesen der Anfrage.");
            }
            catch (JsonException)
            {
                result = new CommandResult(false, "Ungültige Anfrage.");
            }

            var response = (result.Success ? "OK " : "ERR ") + result.Message.ReplaceLineEndings(" ");
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(response);
            if (OperatingSystem.IsWindows())
            {
                // Ohne Drain verwirft Windows ungelesene Daten beim Schließen des Server-Endes.
                pipe.WaitForPipeDrain();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try
                {
                    await Task.WhenAny(_loop, Task.Delay(2000));
                }
                catch
                {
                }
            }

            _cts.Dispose();
        }
    }
}
