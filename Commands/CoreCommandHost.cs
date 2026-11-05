using RSEV.Utilities.Commands;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Commands
{
    public readonly record struct CommandResult(bool Success, string Message);

    /// <summary>
    /// Zentraler Einstiegspunkt für alle Core-Commands. Konsole, Named Pipe (CLI) oder andere Hosts
    /// rufen ausschließlich <see cref="ExecuteAsync(IReadOnlyList{string})"/> auf; jeder hier registrierte
    /// Command ist dadurch automatisch auf allen Wegen verfügbar.
    /// </summary>
    public sealed class CoreCommandHost
    {
        private readonly CommandHandler _handler = new();
        private readonly IRuntimeContext _context;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public CoreCommandHost(IRuntimeContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _handler.Register(new AdapterLifecycleControl());
            _handler.Register(CoreLifecycleControl.Shutdown());
            _handler.Register(CoreLifecycleControl.Restart());
        }

        public void Register(ICommand command) => _handler.Register(command);

        public IEnumerable<ICommand> GetAll() => _handler.GetAll().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>Zerlegt eine Eingabezeile in Tokens; Teile in doppelten Anführungszeichen bleiben zusammen.</summary>
        public static string[] Tokenize(string line)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;
            var hasToken = false;
            foreach (var ch in line)
            {
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(ch) && !inQuotes)
                {
                    if (hasToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                }
                else
                {
                    current.Append(ch);
                    hasToken = true;
                }
            }

            if (hasToken)
            {
                tokens.Add(current.ToString());
            }

            return tokens.ToArray();
        }

        public Task<CommandResult> ExecuteLineAsync(string line) => ExecuteAsync(Tokenize(line));

        /// <summary>Führt <c>tokens[0]</c> als Command mit den restlichen Tokens als Argumenten aus.</summary>
        public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> tokens)
        {
            if (tokens is null || tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            {
                return new CommandResult(false, "Kein Command angegeben. 'help' listet alle Commands.");
            }

            var name = tokens[0].Trim();
            if (string.Equals(name, "help", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandResult(true, string.Join(" | ", GetAll().Select(c => $"{c.Name}: {c.Description}")));
            }

            if (!_handler.Exists(name))
            {
                return new CommandResult(false, $"Unbekannter Command '{name}'.");
            }

            await _gate.WaitAsync();
            try
            {
                await _handler.ExecuteAsync(name, _context, tokens.Skip(1).ToArray());
                return new CommandResult(true, $"Command '{name}' ausgeführt (Details im Log).");
            }
            catch (Exception ex)
            {
                _context.Logger.LogError($"[Commands] '{name}' fehlgeschlagen: {ex}");
                return new CommandResult(false, $"Command '{name}' fehlgeschlagen: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
