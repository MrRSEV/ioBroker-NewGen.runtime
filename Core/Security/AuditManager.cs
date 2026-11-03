using System;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker.Core.Security
{
    public sealed class AuditManager
    {
        private readonly SecurityDatabase _database;
        private readonly bool _enabled;

        public AuditManager(SecurityDatabase database, bool enabled)
        {
            _database = database;
            _enabled = enabled;
        }

        /// <summary>
        /// Schreibt einen Audit-Eintrag. Details dürfen keine Geheimnisse (Passwörter, Token, Keys) enthalten.
        /// </summary>
        public async Task WriteAsync(long? userId, string action, string? details = null, CancellationToken cancellationToken = default)
        {
            if (!_enabled)
            {
                return;
            }

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO AuditLog (UserId, Action, TimestampUtc, Details) VALUES ($user, $action, $ts, $details)";
            command.AddParameter("$user", userId);
            command.AddParameter("$action", action);
            command.AddParameter("$ts", SecurityDatabase.ToDbTime(DateTime.UtcNow));
            command.AddParameter("$details", details);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<long> CountAsync(string? action = null, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AuditLog WHERE $action IS NULL OR Action = $action";
            command.AddParameter("$action", action);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
    }
}
