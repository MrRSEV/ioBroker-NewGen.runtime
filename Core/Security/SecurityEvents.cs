using System;

namespace ioBroker.Core.Security
{
    public abstract class SecurityEventArgs : EventArgs
    {
        protected SecurityEventArgs(long? userId, string? username, string? details = null)
        {
            UserId = userId;
            Username = username;
            Details = details;
            TimestampUtc = DateTime.UtcNow;
        }

        public long? UserId { get; }

        public string? Username { get; }

        public string? Details { get; }

        public DateTime TimestampUtc { get; }
    }

    public sealed class UserCreatedEvent(long userId, string username) : SecurityEventArgs(userId, username);

    public sealed class UserDeletedEvent(long userId, string username) : SecurityEventArgs(userId, username);

    public sealed class UserLoginEvent(long userId, string username, string? remoteAddress)
        : SecurityEventArgs(userId, username, remoteAddress);

    public sealed class UserLogoutEvent(long userId, string username) : SecurityEventArgs(userId, username);

    public sealed class RoleAssignedEvent(long userId, string username, string role)
        : SecurityEventArgs(userId, username, role);

    public sealed class RoleRemovedEvent(long userId, string username, string role)
        : SecurityEventArgs(userId, username, role);

    public sealed class SecurityBootstrapCreated(long userId, string username) : SecurityEventArgs(userId, username);
}
