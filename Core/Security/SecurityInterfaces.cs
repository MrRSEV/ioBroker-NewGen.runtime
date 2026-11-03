using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker.Core.Security
{
    public interface IUserManager
    {
        Task<SecurityUser?> FindAsync(string username, CancellationToken cancellationToken = default);

        Task<SecurityUser> CreateAsync(
            string username,
            string password,
            string? displayName = null,
            IEnumerable<string>? roles = null,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default);

        Task SetPasswordAsync(
            string username,
            string newPassword,
            bool mustChangePassword = false,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetRolesAsync(string username, CancellationToken cancellationToken = default);
    }

    public interface IAuthenticationProvider
    {
        Task<LoginResult> LoginAsync(
            string username,
            string password,
            string? remoteAddress = null,
            CancellationToken cancellationToken = default);

        Task LogoutAsync(string token, CancellationToken cancellationToken = default);

        Task<SecuritySession?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default);
    }

    public interface IAuthorizationProvider
    {
        Task<bool> HasPermissionAsync(long userId, string permission, CancellationToken cancellationToken = default);
    }
}
