using System.Threading.Tasks;
using ioBroker.Core.Security;
using RSEV.Utilities.Events;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Core.Events
{
    /// <summary>
    /// Zentraler EventBus auf Basis der RSEV.Utilities-Implementierung.
    /// Es gibt keine lokale Handler-/Listener-Verwaltung.
    /// </summary>
    public sealed class SystemEventBus : BaseEventBus
    {
        public const string StateChangedEventName = "state.changed";
        public const string AdapterLifecycleEventName = "adapter.lifecycle";

        public SystemEventBus()
            : base("SystemEventBus", "RSEV.Utilities-basierter EventBus für State- und Adapterereignisse.")
        {
            StateChanged = new EventRegistry<StateChangedEventArgs>(StateChangedEventName);
            AdapterLifecycle = new EventRegistry<AdapterLifecycleEventArgs>(AdapterLifecycleEventName);
            RegisterEventRegistry(StateChanged);
            RegisterEventRegistry(AdapterLifecycle);

            UserCreated = RegisterSecurity<UserCreatedEvent>("security.user.created");
            UserDeleted = RegisterSecurity<UserDeletedEvent>("security.user.deleted");
            UserLogin = RegisterSecurity<UserLoginEvent>("security.user.login");
            UserLogout = RegisterSecurity<UserLogoutEvent>("security.user.logout");
            RoleAssigned = RegisterSecurity<RoleAssignedEvent>("security.role.assigned");
            RoleRemoved = RegisterSecurity<RoleRemovedEvent>("security.role.removed");
            SecurityBootstrap = RegisterSecurity<SecurityBootstrapCreated>("security.bootstrap.created");
        }

        public EventRegistry<UserCreatedEvent> UserCreated { get; }

        public EventRegistry<UserDeletedEvent> UserDeleted { get; }

        public EventRegistry<UserLoginEvent> UserLogin { get; }

        public EventRegistry<UserLogoutEvent> UserLogout { get; }

        public EventRegistry<RoleAssignedEvent> RoleAssigned { get; }

        public EventRegistry<RoleRemovedEvent> RoleRemoved { get; }

        public EventRegistry<SecurityBootstrapCreated> SecurityBootstrap { get; }

        private EventRegistry<T> RegisterSecurity<T>(string name) where T : SecurityEventArgs
        {
            var registry = new EventRegistry<T>(name);
            RegisterEventRegistry(registry);
            return registry;
        }

        public EventRegistry<StateChangedEventArgs> StateChanged { get; }

        public EventRegistry<AdapterLifecycleEventArgs> AdapterLifecycle { get; }

        public void PublishStateChanged(object sender, StateChangedEventArgs args)
        {
            StateChanged.Publish(sender, args);
        }

        public Task PublishStateChangedAsync(object sender, StateChangedEventArgs args)
        {
            return StateChanged.PublishAsync(sender, args);
        }

        public void PublishAdapterLifecycle(object sender, AdapterLifecycleEventArgs args)
        {
            AdapterLifecycle.Publish(sender, args);
        }

        public Task PublishAdapterLifecycleAsync(object sender, AdapterLifecycleEventArgs args)
        {
            return AdapterLifecycle.PublishAsync(sender, args);
        }

        protected override void OnInitialize(IRuntimeContext context)
        {
            context.Logger.LogInfo("[SystemEventBus] RSEV.Utilities-EventBus initialisiert.");
        }

        protected override Task OnInitializeAsync(IRuntimeContext context)
        {
            OnInitialize(context);
            return Task.CompletedTask;
        }

        protected override void OnShutdown(IRuntimeContext context)
        {
            context.Logger.LogInfo("[SystemEventBus] Shutdown.");
        }

        protected override Task OnShutdownAsync(IRuntimeContext context)
        {
            OnShutdown(context);
            return Task.CompletedTask;
        }
    }
}
