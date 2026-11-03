using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Matrix;
using RSEV.Utilities.Events;

namespace ioBroker.Core.Security
{
    /// <summary>
    /// Spiegelt ausschließlich unkritische Sicherheitszustände in die Matrix.
    /// Geheimnisse (Passwörter, Hashes, Token, API-Keys) werden nie gespiegelt.
    /// </summary>
    public sealed class SecurityMatrixMirror
    {
        public const string ActiveSessionsState = "system.security.activeSessions";
        public const string FailedLoginsState = "system.security.failedLogins";

        private static readonly string[] Forbidden = { "password", "passwordHash", "token", "apiKey", "secret" };
        private static readonly Regex InvalidNameChars = new("[^A-Za-z0-9_-]", RegexOptions.Compiled);

        private readonly SemanticStateMatrix _matrix;
        private readonly UserManager _users;
        private readonly SessionManager _sessions;

        public SecurityMatrixMirror(SemanticStateMatrix matrix, UserManager users, SessionManager sessions)
        {
            _matrix = matrix;
            _users = users;
            _sessions = sessions;
        }

        public static string UserState(string username, string leaf) =>
            $"system.security.users.{InvalidNameChars.Replace(username, "_")}.{leaf}";

        public async Task RefreshUserAsync(string username, bool userDeleted = false)
        {
            var user = userDeleted ? null : await _users.FindAsync(username).ConfigureAwait(false);
            var online = user is not null && await _sessions.CountActiveAsync(user.Id).ConfigureAwait(false) > 0;

            Write(UserState(username, "online"), online);
            Write(UserState(username, "lastLogin"), user?.LastLoginUtc?.ToString("O") ?? string.Empty);
            await RefreshActiveSessionsAsync().ConfigureAwait(false);
        }

        public async Task RefreshActiveSessionsAsync()
        {
            Write(ActiveSessionsState, await _sessions.CountActiveAsync().ConfigureAwait(false));
        }

        public void WriteFailedLogins(long count) => Write(FailedLoginsState, count);

        public void Attach(ioBroker_NewGen.Core.Events.SystemEventBus bus, Authentication authentication)
        {
            bus.UserLogin.Subscribe(new DelegateHandler<UserLoginEvent>(e => RefreshUserAsync(e.Username!)));
            bus.UserLogout.Subscribe(new DelegateHandler<UserLogoutEvent>(e => RefreshUserAsync(e.Username!)));
            bus.UserDeleted.Subscribe(new DelegateHandler<UserDeletedEvent>(e => RefreshUserAsync(e.Username!, userDeleted: true)));
            authentication.FailedLoginsChanged += WriteFailedLogins;
        }

        private void Write(string stateId, object value)
        {
            foreach (var word in Forbidden)
            {
                if (stateId.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"State '{stateId}' darf nicht in die Matrix gespiegelt werden.");
                }
            }

            _matrix.SetState(stateId, value);
        }

        private sealed class DelegateHandler<T> : BaseEventHandler<T> where T : SecurityEventArgs
        {
            private readonly Func<T, Task> _callback;

            public DelegateHandler(Func<T, Task> callback)
            {
                _callback = callback;
            }

            protected override void OnHandle(object sender, T args)
            {
                // Microsoft.Data.Sqlite arbeitet intern synchron, daher entsteht hier kein Deadlock.
                _callback(args).GetAwaiter().GetResult();
            }
        }
    }
}
