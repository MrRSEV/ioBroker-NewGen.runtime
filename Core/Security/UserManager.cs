using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker.Core.Security
{
    public sealed class UserManager : IUserManager
    {
        private const int BcryptWorkFactor = 11;

        private const string SelectUser =
            "SELECT Id, Username, DisplayName, IsEnabled, MustChangePassword, CreatedUtc, LastLoginUtc FROM Users";

        private readonly SecurityDatabase _database;
        private readonly AuditManager _audit;
        private readonly ISecurityEventPublisher _events;
        private readonly RoleManager _roleManager;
        private readonly int _minPasswordLength;

        public UserManager(
            SecurityDatabase database,
            AuditManager audit,
            ISecurityEventPublisher events,
            RoleManager roleManager,
            int minPasswordLength)
        {
            _database = database;
            _audit = audit;
            _events = events;
            _roleManager = roleManager;
            _minPasswordLength = minPasswordLength;
        }

        /// <summary>
        /// Gültiger BCrypt-Hash, gegen den bei unbekannten Benutzern geprüft wird (Timing-Angleichung).
        /// </summary>
        internal static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(
            Guid.NewGuid().ToString("N"),
            BcryptWorkFactor);

        public async Task<SecurityUser?> FindAsync(string username, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await FindAsync(connection, username, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<SecurityUser>> ListAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"{SelectUser} ORDER BY Username";
            var users = new List<SecurityUser>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                users.Add(ReadUser(reader));
            }

            return users;
        }

        public Task<SecurityUser> CreateAsync(
            string username,
            string password,
            string? displayName = null,
            IEnumerable<string>? roles = null,
            CancellationToken cancellationToken = default)
        {
            return CreateCoreAsync(username, password, displayName, roles, true, false, cancellationToken);
        }

        /// <summary>
        /// Legt einen Benutzer ohne Passwortrichtlinie an (nur für den Bootstrap-Standardadmin).
        /// </summary>
        internal Task<SecurityUser> CreateBootstrapAsync(
            string username,
            string password,
            string role,
            bool mustChangePassword,
            CancellationToken cancellationToken)
        {
            return CreateCoreAsync(username, password, null, new[] { role }, false, mustChangePassword, cancellationToken);
        }

        public async Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var user = await FindAsync(connection, username, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return false;
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM Users WHERE Id = $id";
                command.AddParameter("$id", user.Id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await _audit.WriteAsync(user.Id, "UserDeleted", user.Username, cancellationToken).ConfigureAwait(false);
            _events.Publish(this, new UserDeletedEvent(user.Id, user.Username));
            return true;
        }

        public async Task SetPasswordAsync(
            string username,
            string newPassword,
            bool mustChangePassword = false,
            CancellationToken cancellationToken = default)
        {
            ValidatePassword(newPassword);

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var user = await FindAsync(connection, username, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Benutzer '{username}' existiert nicht.");

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Users SET PasswordHash = $hash, MustChangePassword = $must WHERE Id = $id";
                command.AddParameter("$hash", BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor));
                command.AddParameter("$must", mustChangePassword ? 1 : 0);
                command.AddParameter("$id", user.Id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Bestehende Sitzungen werden nach Passwortänderung ungültig.
            await DeleteSessionsAsync(connection, user.Id, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(user.Id, "PasswordChanged", user.Username, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetEnabledAsync(string username, bool enabled, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var user = await FindAsync(connection, username, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Benutzer '{username}' existiert nicht.");

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Users SET IsEnabled = $enabled WHERE Id = $id";
                command.AddParameter("$enabled", enabled ? 1 : 0);
                command.AddParameter("$id", user.Id);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!enabled)
            {
                await DeleteSessionsAsync(connection, user.Id, cancellationToken).ConfigureAwait(false);
            }

            await _audit.WriteAsync(user.Id, enabled ? "UserEnabled" : "UserDisabled", user.Username, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetDisplayNameAsync(string username, string? displayName, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var user = await FindAsync(connection, username, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Benutzer '{username}' existiert nicht.");
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Users SET DisplayName = $display WHERE Id = $id";
            command.AddParameter("$display", string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());
            command.AddParameter("$id", user.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(user.Id, "UserUpdated", user.Username, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<string>> GetRolesAsync(string username, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.Name FROM Roles r
                JOIN UserRoles ur ON ur.RoleId = r.Id
                JOIN Users u ON u.Id = ur.UserId
                WHERE u.Username = $username ORDER BY r.Name
                """;
            command.AddParameter("$username", username);

            var roles = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                roles.Add(reader.GetString(0));
            }

            return roles;
        }

        /// <summary>
        /// Prüft Benutzername und Passwort. Liefert den Benutzer nur bei gültigem Passwort, unabhängig von IsEnabled.
        /// </summary>
        internal async Task<SecurityUser?> VerifyPasswordAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Username, DisplayName, IsEnabled, MustChangePassword, CreatedUtc, LastLoginUtc, PasswordHash
                FROM Users WHERE Username = $username
                """;
            command.AddParameter("$username", username);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                BCrypt.Net.BCrypt.Verify(password, DummyHash);
                return null;
            }

            var user = ReadUser(reader);
            var hash = reader.GetString(7);
            return BCrypt.Net.BCrypt.Verify(password, hash) ? user : null;
        }

        internal async Task TouchLastLoginAsync(long userId, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Users SET LastLoginUtc = $ts WHERE Id = $id";
            command.AddParameter("$ts", SecurityDatabase.ToDbTime(DateTime.UtcNow));
            command.AddParameter("$id", userId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<SecurityUser> CreateCoreAsync(
            string username,
            string password,
            string? displayName,
            IEnumerable<string>? roles,
            bool enforcePolicy,
            bool mustChangePassword,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("Benutzername darf nicht leer sein.", nameof(username));
            }

            username = username.Trim();
            if (enforcePolicy)
            {
                ValidatePassword(password);
            }
            else if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Passwort darf nicht leer sein.", nameof(password));
            }

            await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
            long id;
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Users (Username, PasswordHash, DisplayName, IsEnabled, MustChangePassword, CreatedUtc)
                    VALUES ($username, $hash, $display, 1, $must, $created);
                    SELECT last_insert_rowid();
                    """;
                command.AddParameter("$username", username);
                command.AddParameter("$hash", BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor));
                command.AddParameter("$display", displayName);
                command.AddParameter("$must", mustChangePassword ? 1 : 0);
                command.AddParameter("$created", SecurityDatabase.ToDbTime(DateTime.UtcNow));
                id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (DbException ex)
            {
                // Eindeutigkeitsverletzung (UNIQUE auf Username) providerneutral erkennen.
                if (await FindAsync(connection, username, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException($"Benutzer '{username}' existiert bereits.", ex);
                }

                throw;
            }

            var user = (await FindAsync(connection, username, cancellationToken).ConfigureAwait(false))!;
            await _audit.WriteAsync(id, "UserCreated", username, cancellationToken).ConfigureAwait(false);
            _events.Publish(this, new UserCreatedEvent(id, username));

            if (roles is not null)
            {
                foreach (var role in roles)
                {
                    await _roleManager.AssignAsync(username, role, cancellationToken).ConfigureAwait(false);
                }
            }

            return user;
        }

        private void ValidatePassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < _minPasswordLength)
            {
                throw new ArgumentException($"Das Passwort muss mindestens {_minPasswordLength} Zeichen lang sein.", nameof(password));
            }

            // BCrypt verarbeitet nur die ersten 72 Bytes.
            if (System.Text.Encoding.UTF8.GetByteCount(password) > 72)
            {
                throw new ArgumentException("Das Passwort darf höchstens 72 Bytes lang sein.", nameof(password));
            }
        }

        private static async Task DeleteSessionsAsync(DbConnection connection, long userId, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions WHERE UserId = $id";
            command.AddParameter("$id", userId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<SecurityUser?> FindAsync(
            DbConnection connection,
            string username,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"{SelectUser} WHERE Username = $username";
            command.AddParameter("$username", username);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(reader) : null;
        }

        private static SecurityUser ReadUser(DbDataReader reader)
        {
            return new SecurityUser(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3) != 0,
                !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                SecurityDatabase.FromDbTime(reader.IsDBNull(5) ? null : reader.GetString(5)) ?? DateTime.UtcNow,
                SecurityDatabase.FromDbTime(reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
    }
}
