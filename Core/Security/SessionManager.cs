using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker.Core.Security
{
    /// <summary>
    /// Verwaltet Bearer-Token-Sitzungen. In der Datenbank wird nur der SHA-256-Hash des Tokens gespeichert.
    /// </summary>
    public sealed class SessionManager
    {
        private readonly SecurityDatabase _database;
        private readonly TimeSpan _timeout;

        public SessionManager(SecurityDatabase database, TimeSpan timeout)
        {
            _database = database;
            _timeout = timeout;
        }

        public async Task<(string Token, SecuritySession Session)> CreateAsync(
            long userId,
            string username,
            string? remoteAddress,
            CancellationToken cancellationToken = default)
        {
            var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var now = DateTime.UtcNow;
            var session = new SecuritySession(HashToken(token), userId, username, now, now, now + _timeout, remoteAddress);

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Sessions (Id, UserId, CreatedUtc, LastAccessUtc, ExpiresUtc, RemoteAddress)
                VALUES ($id, $user, $created, $access, $expires, $remote)
                """;
            command.AddParameter("$id", session.Id);
            command.AddParameter("$user", userId);
            command.AddParameter("$created", SecurityDatabase.ToDbTime(now));
            command.AddParameter("$access", SecurityDatabase.ToDbTime(now));
            command.AddParameter("$expires", SecurityDatabase.ToDbTime(session.ExpiresUtc));
            command.AddParameter("$remote", remoteAddress);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return (token, session);
        }

        /// <summary>
        /// Prüft das Token, verlängert die Sitzung (Sliding Expiration) und liefert sie zurück.
        /// </summary>
        public async Task<SecuritySession?> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var id = HashToken(token);
            var now = DateTime.UtcNow;

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            SecuritySession? session;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT s.UserId, u.Username, s.CreatedUtc, s.ExpiresUtc, s.RemoteAddress
                    FROM Sessions s JOIN Users u ON u.Id = s.UserId
                    WHERE s.Id = $id AND u.IsEnabled = 1
                    """;
                command.AddParameter("$id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var expires = SecurityDatabase.FromDbTime(reader.GetString(3)) ?? DateTime.MinValue;
                if (expires <= now)
                {
                    session = null;
                }
                else
                {
                    session = new SecuritySession(
                        id,
                        reader.GetInt64(0),
                        reader.GetString(1),
                        SecurityDatabase.FromDbTime(reader.GetString(2)) ?? now,
                        now,
                        now + _timeout,
                        reader.IsDBNull(4) ? null : reader.GetString(4));
                }
            }

            await using var update = connection.CreateCommand();
            if (session is null)
            {
                update.CommandText = "DELETE FROM Sessions WHERE Id = $id";
                update.AddParameter("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            update.CommandText = "UPDATE Sessions SET LastAccessUtc = $access, ExpiresUtc = $expires WHERE Id = $id";
            update.AddParameter("$access", SecurityDatabase.ToDbTime(session.LastAccessUtc));
            update.AddParameter("$expires", SecurityDatabase.ToDbTime(session.ExpiresUtc));
            update.AddParameter("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }

        /// <summary>
        /// Beendet die Sitzung und liefert Benutzer-ID und Name, sofern die Sitzung existierte.
        /// </summary>
        public async Task<(long UserId, string Username)?> RevokeAsync(string token, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM Sessions WHERE Id = $id
                RETURNING UserId, (SELECT Username FROM Users WHERE Id = UserId)
                """;
            command.AddParameter("$id", HashToken(token));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? (reader.GetInt64(0), reader.GetString(1))
                : null;
        }

        public async Task<int> CountActiveAsync(long? userId = null, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM Sessions
                WHERE ExpiresUtc > $now AND ($user IS NULL OR UserId = $user)
                """;
            command.AddParameter("$now", SecurityDatabase.ToDbTime(DateTime.UtcNow));
            command.AddParameter("$user", userId);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions WHERE ExpiresUtc <= $now";
            command.AddParameter("$now", SecurityDatabase.ToDbTime(DateTime.UtcNow));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string HashToken(string token)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
