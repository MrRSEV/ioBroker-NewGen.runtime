using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Thread-sicherer Rollen- und Berechtigungsdienst für State-Zugriffe.
    /// Ohne passende Regel bleibt der bisherige kompatible Standard erhalten:
    /// Zugriff wird gewährt. Explizite Benutzerregeln haben Vorrang vor
    /// Rollenregeln; mehrere passende Rollenregeln werden sicherheitsbewusst
    /// mit Deny-wins ausgewertet.
    /// </summary>
    public sealed class DefaultRoleService : IRoleManagementService
    {
        private sealed record Permission(bool CanRead, bool CanWrite);

        private sealed class RoleEntry
        {
            public string Description { get; set; } = string.Empty;

            public Dictionary<string, Permission> Permissions { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedRole
        {
            public string Name { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public Dictionary<string, PersistedPermission> Permissions { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedPermission
        {
            public bool CanRead { get; set; }

            public bool CanWrite { get; set; }
        }

        private sealed class PersistedModel
        {
            public List<PersistedRole> Roles { get; set; } = new();

            public Dictionary<string, string[]> UserRoles { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);

            public List<UserStatePermission> UserPermissions { get; set; } = new();
        }

        private readonly object _sync = new();
        private readonly Dictionary<string, Permission> _userPermissions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RoleEntry> _roles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _userRoles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _persistencePath;

        public DefaultRoleService(string? persistencePath = null)
        {
            _persistencePath = string.IsNullOrWhiteSpace(persistencePath)
                ? null
                : Path.GetFullPath(persistencePath);

            LoadPersistedState();
        }

        /// <summary>
        /// Hinterlegt explizite Lese-/Schreibrechte für einen Benutzer auf einen State.
        /// Ohne expliziten Eintrag wird voller Zugriff gewährt.
        /// </summary>
        public void SetPermissions(string user, string stateId, bool canRead, bool canWrite)
        {
            var normalizedUser = Normalize(user, nameof(user));
            var normalizedState = Normalize(stateId, nameof(stateId));

            lock (_sync)
            {
                _userPermissions[BuildKey(normalizedUser, normalizedState)] = new Permission(canRead, canWrite);
                PersistNoThrow();
            }
        }

        public bool RemoveUserPermissions(string user, string stateId)
        {
            var normalizedUser = Normalize(user, nameof(user));
            var normalizedState = Normalize(stateId, nameof(stateId));

            lock (_sync)
            {
                var removed = _userPermissions.Remove(BuildKey(normalizedUser, normalizedState));
                if (removed)
                {
                    PersistNoThrow();
                }

                return removed;
            }
        }

        public bool CanRead(string user, string stateId)
        {
            return Evaluate(user, stateId, permission => permission.CanRead);
        }

        public bool CanWrite(string user, string stateId)
        {
            return Evaluate(user, stateId, permission => permission.CanWrite);
        }

        public IReadOnlyCollection<RoleDefinition> GetRoles()
        {
            lock (_sync)
            {
                return _roles
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new RoleDefinition(
                        pair.Key,
                        pair.Value.Description,
                        pair.Value.Permissions
                            .OrderBy(permission => permission.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(permission => new RoleStatePermission(
                                pair.Key,
                                permission.Key,
                                permission.Value.CanRead,
                                permission.Value.CanWrite))
                            .ToArray()))
                    .ToArray();
            }
        }

        public IReadOnlyCollection<UserRoleAssignment> GetUserRoleAssignments()
        {
            lock (_sync)
            {
                return _userRoles
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(pair => pair.Value
                        .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                        .Select(role => new UserRoleAssignment(pair.Key, role)))
                    .ToArray();
            }
        }

        public IReadOnlyCollection<UserStatePermission> GetUserPermissions()
        {
            lock (_sync)
            {
                return _userPermissions
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => SplitKey(pair.Key, pair.Value))
                    .ToArray();
            }
        }

        public bool UpsertRole(string name, string description = "")
        {
            var normalizedName = Normalize(name, nameof(name));
            var normalizedDescription = description?.Trim() ?? string.Empty;

            lock (_sync)
            {
                if (_roles.TryGetValue(normalizedName, out var existing))
                {
                    existing.Description = normalizedDescription;
                }
                else
                {
                    _roles[normalizedName] = new RoleEntry { Description = normalizedDescription };
                }

                PersistNoThrow();
                return true;
            }
        }

        public bool RemoveRole(string name)
        {
            var normalizedName = Normalize(name, nameof(name));

            lock (_sync)
            {
                var removed = _roles.Remove(normalizedName);
                foreach (var roles in _userRoles.Values)
                {
                    roles.Remove(normalizedName);
                }

                if (removed)
                {
                    PersistNoThrow();
                }

                return removed;
            }
        }

        public bool AssignRole(string user, string role)
        {
            var normalizedUser = Normalize(user, nameof(user));
            var normalizedRole = Normalize(role, nameof(role));

            lock (_sync)
            {
                if (!_roles.ContainsKey(normalizedRole))
                {
                    return false;
                }

                if (!_userRoles.TryGetValue(normalizedUser, out var roles))
                {
                    roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _userRoles[normalizedUser] = roles;
                }

                var added = roles.Add(normalizedRole);
                if (added)
                {
                    PersistNoThrow();
                }

                return added;
            }
        }

        public bool RevokeRole(string user, string role)
        {
            var normalizedUser = Normalize(user, nameof(user));
            var normalizedRole = Normalize(role, nameof(role));

            lock (_sync)
            {
                if (!_userRoles.TryGetValue(normalizedUser, out var roles))
                {
                    return false;
                }

                var removed = roles.Remove(normalizedRole);
                if (roles.Count == 0)
                {
                    _userRoles.Remove(normalizedUser);
                }

                if (removed)
                {
                    PersistNoThrow();
                }

                return removed;
            }
        }

        public bool SetRolePermissions(string role, string stateId, bool canRead, bool canWrite)
        {
            var normalizedRole = Normalize(role, nameof(role));
            var normalizedState = Normalize(stateId, nameof(stateId));

            lock (_sync)
            {
                if (!_roles.TryGetValue(normalizedRole, out var roleEntry))
                {
                    return false;
                }

                roleEntry.Permissions[normalizedState] = new Permission(canRead, canWrite);
                PersistNoThrow();
                return true;
            }
        }

        public bool RemoveRolePermissions(string role, string stateId)
        {
            var normalizedRole = Normalize(role, nameof(role));
            var normalizedState = Normalize(stateId, nameof(stateId));

            lock (_sync)
            {
                if (!_roles.TryGetValue(normalizedRole, out var roleEntry))
                {
                    return false;
                }

                var removed = roleEntry.Permissions.Remove(normalizedState);
                if (removed)
                {
                    PersistNoThrow();
                }

                return removed;
            }
        }

        private bool Evaluate(string user, string stateId, Func<Permission, bool> selector)
        {
            // Adapter-Implementierungen dürfen vor der Registry-Zuweisung bereits
            // Frames senden. Ein leerer Adapter-ID-Wert bleibt deshalb der
            // kompatible Systembenutzer und darf die Matrix nicht blockieren.
            var normalizedUser = NormalizeUser(user);
            var normalizedState = Normalize(stateId, nameof(stateId));

            lock (_sync)
            {
                if (_userPermissions.TryGetValue(BuildKey(normalizedUser, normalizedState), out var explicitPermission))
                {
                    return selector(explicitPermission);
                }

                var rolePermissions = new List<Permission>();
                if (_userRoles.TryGetValue(normalizedUser, out var roles))
                {
                    foreach (var role in roles)
                    {
                        if (!_roles.TryGetValue(role, out var roleEntry))
                        {
                            continue;
                        }

                        if (roleEntry.Permissions.TryGetValue(normalizedState, out var statePermission))
                        {
                            rolePermissions.Add(statePermission);
                        }

                        if (roleEntry.Permissions.TryGetValue("*", out var globalPermission))
                        {
                            rolePermissions.Add(globalPermission);
                        }
                    }
                }

                return rolePermissions.Count == 0 || rolePermissions.All(selector);
            }
        }

        private void LoadPersistedState()
        {
            if (_persistencePath is null || !File.Exists(_persistencePath))
            {
                return;
            }

            try
            {
                var model = JsonSerializer.Deserialize<PersistedModel>(File.ReadAllText(_persistencePath));
                if (model is null)
                {
                    return;
                }

                lock (_sync)
                {
                    foreach (var role in model.Roles ?? new List<PersistedRole>())
                    {
                        if (string.IsNullOrWhiteSpace(role.Name))
                        {
                            continue;
                        }

                        var entry = new RoleEntry { Description = role.Description ?? string.Empty };
                        foreach (var permission in role.Permissions ?? new Dictionary<string, PersistedPermission>())
                        {
                            if (!string.IsNullOrWhiteSpace(permission.Key) && permission.Value is not null)
                            {
                                entry.Permissions[permission.Key.Trim()] = new Permission(
                                    permission.Value.CanRead,
                                    permission.Value.CanWrite);
                            }
                        }

                        _roles[role.Name.Trim()] = entry;
                    }

                    foreach (var assignment in model.UserRoles ?? new Dictionary<string, string[]>())
                    {
                        if (string.IsNullOrWhiteSpace(assignment.Key))
                        {
                            continue;
                        }

                        var validRoles = (assignment.Value ?? Array.Empty<string>())
                            .Where(role => !string.IsNullOrWhiteSpace(role) && _roles.ContainsKey(role.Trim()))
                            .Select(role => role.Trim());
                        _userRoles[assignment.Key.Trim()] = new HashSet<string>(validRoles, StringComparer.OrdinalIgnoreCase);
                    }

                    foreach (var permission in model.UserPermissions ?? new List<UserStatePermission>())
                    {
                        if (!string.IsNullOrWhiteSpace(permission.User) && !string.IsNullOrWhiteSpace(permission.StateId))
                        {
                            _userPermissions[BuildKey(permission.User.Trim(), permission.StateId.Trim())] =
                                new Permission(permission.CanRead, permission.CanWrite);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Eine defekte optionale Rollenablage darf den Runtime-Start nicht verhindern.
            }
            catch (IOException)
            {
                // Die Ablage ist optional; Änderungen können im laufenden Prozess weiter genutzt werden.
            }
        }

        private void PersistNoThrow()
        {
            if (_persistencePath is null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_persistencePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                PersistedModel model;
                model = new PersistedModel
                {
                    Roles = _roles.Select(role => new PersistedRole
                    {
                        Name = role.Key,
                        Description = role.Value.Description,
                        Permissions = role.Value.Permissions.ToDictionary(
                            permission => permission.Key,
                            permission => new PersistedPermission
                            {
                                CanRead = permission.Value.CanRead,
                                CanWrite = permission.Value.CanWrite
                            },
                            StringComparer.OrdinalIgnoreCase)
                    }).ToList(),
                    UserRoles = _userRoles.ToDictionary(
                        assignment => assignment.Key,
                        assignment => assignment.Value.OrderBy(role => role, StringComparer.OrdinalIgnoreCase).ToArray(),
                        StringComparer.OrdinalIgnoreCase),
                    UserPermissions = _userPermissions
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => SplitKey(pair.Key, pair.Value))
                        .ToList()
                };

                var temporaryPath = _persistencePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _persistencePath, overwrite: true);
            }
            catch (IOException)
            {
                // Die Laufzeitberechtigungen bleiben gültig, selbst wenn die optionale
                // persistente Ablage vorübergehend nicht beschreibbar ist.
            }
            catch (UnauthorizedAccessException)
            {
                // Siehe IOException: die In-Memory-Regeln bleiben aktiv.
            }
        }

        private static string Normalize(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Wert darf nicht leer sein.", parameterName);
            }

            return value.Trim();
        }

        private static string NormalizeUser(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "system" : value.Trim();
        }

        private static string BuildKey(string user, string stateId) => $"{user}\u001f{stateId}";

        private static UserStatePermission SplitKey(string key, Permission permission)
        {
            var separator = key.IndexOf('\u001f');
            if (separator < 0)
            {
                return new UserStatePermission(key, string.Empty, permission.CanRead, permission.CanWrite);
            }

            return new UserStatePermission(
                key[..separator],
                key[(separator + 1)..],
                permission.CanRead,
                permission.CanWrite);
        }
    }
}
