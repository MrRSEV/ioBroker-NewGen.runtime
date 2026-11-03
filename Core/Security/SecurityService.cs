using System;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Events;

namespace ioBroker.Core.Security
{
    public sealed class SystemEventBusSecurityPublisher : ISecurityEventPublisher
    {
        private readonly SystemEventBus _bus;

        public SystemEventBusSecurityPublisher(SystemEventBus bus)
        {
            _bus = bus;
        }

        public void Publish(object sender, SecurityEventArgs args)
        {
            switch (args)
            {
                case UserCreatedEvent e: _bus.UserCreated.Publish(sender, e); break;
                case UserDeletedEvent e: _bus.UserDeleted.Publish(sender, e); break;
                case UserLoginEvent e: _bus.UserLogin.Publish(sender, e); break;
                case UserLogoutEvent e: _bus.UserLogout.Publish(sender, e); break;
                case RoleAssignedEvent e: _bus.RoleAssigned.Publish(sender, e); break;
                case RoleRemovedEvent e: _bus.RoleRemoved.Publish(sender, e); break;
                case SecurityBootstrapCreated e: _bus.SecurityBootstrap.Publish(sender, e); break;
            }
        }
    }

    /// <summary>
    /// Fassade und Einstiegspunkt des Security-Moduls.
    /// </summary>
    public sealed class SecurityService
    {
        private readonly ISecurityEventPublisher _events;

        public SecurityService(SecurityOptions options, ISecurityEventPublisher? events = null)
        {
            Options = options;
            _events = events ?? NullSecurityEventPublisher.Instance;

            Database = new SecurityDatabase(options.DatabasePath);
            Audit = new AuditManager(Database, options.AuditLogEnabled);
            Roles = new RoleManager(Database, Audit, _events);
            Users = new UserManager(Database, Audit, _events, Roles, options.MinPasswordLength);
            Permissions = new PermissionManager(Database);
            Sessions = new SessionManager(Database, TimeSpan.FromMinutes(options.SessionTimeoutMinutes));
            Authentication = new Authentication(Users, Sessions, Audit, _events);
        }

        public SecurityOptions Options { get; }

        public SecurityDatabase Database { get; }

        public AuditManager Audit { get; }

        public RoleManager Roles { get; }

        public UserManager Users { get; }

        public PermissionManager Permissions { get; }

        public SessionManager Sessions { get; }

        public Authentication Authentication { get; }

        public SecurityMatrixMirror? Mirror { get; private set; }

        public IAuthorizationProvider Authorization => Permissions;

        /// <summary>
        /// Legt Schema und Seed an und erstellt – falls noch kein Benutzer existiert – den Standardadministrator.
        /// </summary>
        /// <returns><c>true</c>, wenn der Standardadministrator neu angelegt wurde.</returns>
        public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Database.Initialize();
            await Sessions.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);

            if (!Options.CreateDefaultAdmin || (await Users.ListAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                return false;
            }

            var admin = await Users.CreateBootstrapAsync(
                Options.DefaultUsername,
                Options.DefaultPassword,
                SecurityRoles.Administrator,
                Options.ForcePasswordChange,
                cancellationToken).ConfigureAwait(false);
            await Audit.WriteAsync(admin.Id, "SecurityBootstrapCreated", admin.Username, cancellationToken).ConfigureAwait(false);
            _events.Publish(this, new SecurityBootstrapCreated(admin.Id, admin.Username));
            return true;
        }

        public void AttachMirror(SecurityMatrixMirror mirror, SystemEventBus bus)
        {
            Mirror = mirror;
            mirror.Attach(bus, Authentication);
        }
    }
}
