using System;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker.Core.Security
{
    public sealed class Authentication : IAuthenticationProvider
    {
        private readonly UserManager _users;
        private readonly SessionManager _sessions;
        private readonly AuditManager _audit;
        private readonly ISecurityEventPublisher _events;
        private long _failedLogins;

        public Authentication(
            UserManager users,
            SessionManager sessions,
            AuditManager audit,
            ISecurityEventPublisher events)
        {
            _users = users;
            _sessions = sessions;
            _audit = audit;
            _events = events;
        }

        public long FailedLogins => Interlocked.Read(ref _failedLogins);

        public event Action<long>? FailedLoginsChanged;

        public async Task<LoginResult> LoginAsync(
            string username,
            string password,
            string? remoteAddress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                return await FailAsync(null, remoteAddress, cancellationToken).ConfigureAwait(false);
            }

            var user = await _users.VerifyPasswordAsync(username.Trim(), password, cancellationToken).ConfigureAwait(false);
            if (user is null || !user.IsEnabled)
            {
                return await FailAsync(user?.Id, remoteAddress, cancellationToken).ConfigureAwait(false);
            }

            var (token, session) = await _sessions.CreateAsync(user.Id, user.Username, remoteAddress, cancellationToken).ConfigureAwait(false);
            await _users.TouchLastLoginAsync(user.Id, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(user.Id, "Login", remoteAddress, cancellationToken).ConfigureAwait(false);
            _events.Publish(this, new UserLoginEvent(user.Id, user.Username, remoteAddress));

            return new LoginResult(true, token, user with { LastLoginUtc = DateTime.UtcNow }, session.ExpiresUtc, user.MustChangePassword, null);
        }

        public async Task LogoutAsync(string token, CancellationToken cancellationToken = default)
        {
            var revoked = await _sessions.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
            if (revoked is null)
            {
                return;
            }

            await _audit.WriteAsync(revoked.Value.UserId, "Logout", null, cancellationToken).ConfigureAwait(false);
            _events.Publish(this, new UserLogoutEvent(revoked.Value.UserId, revoked.Value.Username));
        }

        public Task<SecuritySession?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default)
        {
            return _sessions.ValidateAsync(token, cancellationToken);
        }

        private async Task<LoginResult> FailAsync(long? userId, string? remoteAddress, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _failedLogins);
            // Der versuchte Benutzername wird bewusst nicht protokolliert (könnte ein versehentlich eingegebenes Passwort sein).
            await _audit.WriteAsync(userId, "LoginFailed", remoteAddress, cancellationToken).ConfigureAwait(false);
            FailedLoginsChanged?.Invoke(count);
            return LoginResult.Failed("Benutzername oder Passwort ungültig.");
        }
    }
}
