using System;
using System.Collections.Generic;
using System.Linq;

namespace ioBroker.Core.Security
{
    public sealed record SecurityUser(
        long Id,
        string Username,
        string? DisplayName,
        bool IsEnabled,
        bool MustChangePassword,
        DateTime CreatedUtc,
        DateTime? LastLoginUtc);

    public sealed record SecuritySession(
        string Id,
        long UserId,
        string Username,
        DateTime CreatedUtc,
        DateTime LastAccessUtc,
        DateTime ExpiresUtc,
        string? RemoteAddress);

    public sealed record LoginResult(
        bool Success,
        string? Token,
        SecurityUser? User,
        DateTime? ExpiresUtc,
        bool MustChangePassword,
        string? Error)
    {
        public static LoginResult Failed(string error) => new(false, null, null, null, false, error);
    }

    public sealed record SecurityRoleDefinition(
        string Name,
        string Description,
        IReadOnlyList<string> Inherits,
        IReadOnlyList<string> Permissions);

    public sealed class SecurityOptions
    {
        public string DatabasePath { get; set; } = System.IO.Path.Combine("data", "security.db");

        public int SessionTimeoutMinutes { get; set; } = 1440;

        public bool AllowAnonymous { get; set; }

        public bool ApiKeysEnabled { get; set; } = true;

        public bool AuditLogEnabled { get; set; } = true;

        public bool MatrixMirrorEnabled { get; set; } = true;

        public int MinPasswordLength { get; set; } = 8;

        public bool CreateDefaultAdmin { get; set; } = true;

        public string DefaultUsername { get; set; } = "admin";

        public string DefaultPassword { get; set; } = "admin";

        public bool ForcePasswordChange { get; set; } = true;
    }
}
