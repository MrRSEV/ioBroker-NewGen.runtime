using System;
using System.Collections.Generic;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Eine explizite Berechtigungsregel für einen Benutzer und einen State.
    /// </summary>
    public sealed record UserStatePermission(
        string User,
        string StateId,
        bool CanRead,
        bool CanWrite);

    /// <summary>
    /// Eine Berechtigungsregel innerhalb einer Rolle. Der StateId-Wert "*"
    /// gilt als globale Regel für alle States.
    /// </summary>
    public sealed record RoleStatePermission(
        string Role,
        string StateId,
        bool CanRead,
        bool CanWrite);

    /// <summary>
    /// Zuordnung eines Benutzers zu einer Rolle.
    /// </summary>
    public sealed record UserRoleAssignment(string User, string Role);

    /// <summary>
    /// Öffentliche Beschreibung einer Rolle inklusive ihrer State-Regeln.
    /// </summary>
    public sealed record RoleDefinition(
        string Name,
        string Description,
        IReadOnlyCollection<RoleStatePermission> Permissions);

    /// <summary>
    /// Erweiterter Vertrag für verwaltbare Benutzer- und Rollenrechte.
    /// IRoleService bleibt bewusst klein, damit externe Implementierungen
    /// weiterhin nur CanRead/CanWrite bereitstellen müssen.
    /// </summary>
    public interface IRoleManagementService : IRoleService
    {
        IReadOnlyCollection<RoleDefinition> GetRoles();

        IReadOnlyCollection<UserRoleAssignment> GetUserRoleAssignments();

        IReadOnlyCollection<UserStatePermission> GetUserPermissions();

        void SetPermissions(string user, string stateId, bool canRead, bool canWrite);

        bool UpsertRole(string name, string description = "");

        bool RemoveRole(string name);

        bool AssignRole(string user, string role);

        bool RevokeRole(string user, string role);

        bool SetRolePermissions(string role, string stateId, bool canRead, bool canWrite);

        bool RemoveRolePermissions(string role, string stateId);

        bool RemoveUserPermissions(string user, string stateId);
    }
}
