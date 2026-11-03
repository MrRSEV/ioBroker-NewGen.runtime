using System;
using System.Collections.Generic;
using System.Linq;

namespace ioBroker.Core.Security
{
    public static class SecurityPermissions
    {
        public const string StateRead = nameof(StateRead);
        public const string StateWrite = nameof(StateWrite);
        public const string EventRead = nameof(EventRead);
        public const string AdapterRead = nameof(AdapterRead);
        public const string AdapterStart = nameof(AdapterStart);
        public const string AdapterStop = nameof(AdapterStop);
        public const string AdapterRestart = nameof(AdapterRestart);
        public const string AdapterInstall = nameof(AdapterInstall);
        public const string AdapterRemove = nameof(AdapterRemove);
        public const string PackageRead = nameof(PackageRead);
        public const string PackageInstall = nameof(PackageInstall);
        public const string PackageRemove = nameof(PackageRemove);
        public const string UserRead = nameof(UserRead);
        public const string UserManage = nameof(UserManage);
        public const string RoleRead = nameof(RoleRead);
        public const string RoleManage = nameof(RoleManage);
        public const string SystemRead = nameof(SystemRead);
        public const string SystemConfigure = nameof(SystemConfigure);
        public const string SystemAdministration = nameof(SystemAdministration);
        public const string SystemShutdown = nameof(SystemShutdown);

        public static IReadOnlyList<string> All { get; } = new[]
        {
            StateRead, StateWrite, EventRead,
            AdapterRead, AdapterStart, AdapterStop, AdapterRestart, AdapterInstall, AdapterRemove,
            PackageRead, PackageInstall, PackageRemove,
            UserRead, UserManage, RoleRead, RoleManage,
            SystemRead, SystemConfigure, SystemAdministration, SystemShutdown
        };
    }

    public static class SecurityRoles
    {
        public const string Viewer = nameof(Viewer);
        public const string Operator = nameof(Operator);
        public const string Developer = nameof(Developer);
        public const string Administrator = nameof(Administrator);

        public static IReadOnlyList<SecurityRoleDefinition> Defaults { get; } = new[]
        {
            new SecurityRoleDefinition(Viewer, "Nur lesender Zugriff", Array.Empty<string>(), new[]
            {
                SecurityPermissions.StateRead, SecurityPermissions.EventRead,
                SecurityPermissions.AdapterRead, SecurityPermissions.SystemRead
            }),
            new SecurityRoleDefinition(Operator, "Normaler Anlagenbetrieb", new[] { Viewer }, new[]
            {
                SecurityPermissions.StateWrite, SecurityPermissions.AdapterStart,
                SecurityPermissions.AdapterStop, SecurityPermissions.AdapterRestart,
                SecurityPermissions.PackageRead
            }),
            new SecurityRoleDefinition(Developer, "Adapter- und Systementwicklung", new[] { Operator }, new[]
            {
                SecurityPermissions.AdapterInstall, SecurityPermissions.AdapterRemove,
                SecurityPermissions.PackageInstall, SecurityPermissions.PackageRemove,
                SecurityPermissions.SystemConfigure
            }),
            new SecurityRoleDefinition(Administrator, "Vollzugriff", new[] { Developer }, new[]
            {
                SecurityPermissions.UserRead, SecurityPermissions.UserManage,
                SecurityPermissions.RoleRead, SecurityPermissions.RoleManage,
                SecurityPermissions.SystemAdministration, SecurityPermissions.SystemShutdown
            })
        };

        /// <summary>
        /// Liefert die Rechte einer Rolle inklusive aller geerbten Rechte.
        /// </summary>
        public static IReadOnlySet<string> Flatten(string roleName)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Collect(roleName, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return result;
        }

        private static void Collect(string roleName, HashSet<string> result, HashSet<string> visited)
        {
            if (!visited.Add(roleName))
            {
                return;
            }

            var role = Defaults.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                return;
            }

            foreach (var permission in role.Permissions)
            {
                result.Add(permission);
            }

            foreach (var parent in role.Inherits)
            {
                Collect(parent, result, visited);
            }
        }
    }
}
