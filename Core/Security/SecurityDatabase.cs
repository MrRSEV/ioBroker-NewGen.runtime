using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RSEV.Utilities.Connection;
using RSEV.Utilities.Database;

namespace ioBroker.Core.Security
{
    /// <summary>
    /// SQLite-Speicher des Security-Moduls (Schema, Seed für Rechte und Standardrollen).
    /// Der Zugriff läuft über RSEV.Utilities (<see cref="SqliteAdapter"/>, <see cref="ConnectionFactory"/>).
    /// </summary>
    public sealed class SecurityDatabase
    {
        private const string Schema = """
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY,
                Username TEXT UNIQUE NOT NULL COLLATE NOCASE,
                PasswordHash TEXT NOT NULL,
                DisplayName TEXT,
                IsEnabled BOOLEAN NOT NULL,
                MustChangePassword BOOLEAN DEFAULT FALSE,
                CreatedUtc TEXT,
                LastLoginUtc TEXT
            );
            CREATE TABLE IF NOT EXISTS Roles (
                Id INTEGER PRIMARY KEY,
                Name TEXT UNIQUE NOT NULL COLLATE NOCASE,
                Description TEXT
            );
            CREATE TABLE IF NOT EXISTS Permissions (
                Id INTEGER PRIMARY KEY,
                Name TEXT UNIQUE NOT NULL
            );
            CREATE TABLE IF NOT EXISTS UserRoles (
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                RoleId INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
                PRIMARY KEY (UserId, RoleId)
            );
            CREATE TABLE IF NOT EXISTS RolePermissions (
                RoleId INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
                PermissionId INTEGER NOT NULL REFERENCES Permissions(Id) ON DELETE CASCADE,
                PRIMARY KEY (RoleId, PermissionId)
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
                CreatedUtc TEXT,
                LastAccessUtc TEXT,
                ExpiresUtc TEXT,
                RemoteAddress TEXT
            );
            CREATE TABLE IF NOT EXISTS ApiKeys (
                Id INTEGER PRIMARY KEY,
                Name TEXT,
                KeyHash TEXT,
                UserId INTEGER REFERENCES Users(Id) ON DELETE CASCADE,
                ExpiresUtc TEXT
            );
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id INTEGER PRIMARY KEY,
                UserId INTEGER,
                Action TEXT,
                TimestampUtc TEXT,
                Details TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Sessions_UserId ON Sessions(UserId);
            CREATE INDEX IF NOT EXISTS IX_AuditLog_TimestampUtc ON AuditLog(TimestampUtc);
            """;

        private const string ProviderInvariantName = "Microsoft.Data.Sqlite";

        private readonly ConnectionFactory _factory = new(new SqliteAdapter());
        private readonly ConnectionConfiguration _configuration;

        static SecurityDatabase()
        {
            // SqliteAdapter löst den Provider über DbProviderFactories auf; dieser muss einmalig registriert werden.
            DbProviderFactories.RegisterFactory(ProviderInvariantName, SqliteFactory.Instance);
        }

        public SecurityDatabase(string path)
        {
            Path = System.IO.Path.GetFullPath(path);

            var builder = SqliteFactory.Instance.CreateConnectionStringBuilder()!;
            builder["Data Source"] = Path;
            builder["Mode"] = "ReadWriteCreate";
            builder["Foreign Keys"] = true;

            _configuration = new ConnectionConfiguration
            {
                ProviderInvariantName = ProviderInvariantName,
                ConnectionString = builder.ConnectionString
            };
        }

        public string Path { get; }

        public void Initialize()
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = _factory.Create(_configuration);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            Execute(connection, transaction, Schema);

            foreach (var permission in SecurityPermissions.All)
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO Permissions (Name) VALUES ($name)", ("$name", permission));
            }

            foreach (var role in SecurityRoles.Defaults)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT OR IGNORE INTO Roles (Name, Description) VALUES ($name, $description)",
                    ("$name", role.Name),
                    ("$description", role.Description));

                // Vererbung wird beim Seed aufgelöst; zur Laufzeit genügt ein einfacher Join.
                foreach (var permission in SecurityRoles.Flatten(role.Name))
                {
                    Execute(
                        connection,
                        transaction,
                        """
                        INSERT OR IGNORE INTO RolePermissions (RoleId, PermissionId)
                        SELECT r.Id, p.Id FROM Roles r, Permissions p WHERE r.Name = $role AND p.Name = $permission
                        """,
                        ("$role", role.Name),
                        ("$permission", permission));
                }
            }

            transaction.Commit();
        }

        public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = _factory.Create(_configuration);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal static string ToDbTime(DateTime value) => value.ToUniversalTime().ToString("O");

        internal static DateTime? FromDbTime(object? value)
        {
            return value is string text && DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }

        private static void Execute(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.AddParameter(name, value);
            }

            command.ExecuteNonQuery();
        }
    }
}
