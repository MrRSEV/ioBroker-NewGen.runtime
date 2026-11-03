using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RSEV.Utilities.Events;
using RSEV.Utilities.Messaging;
using RSEV.Utilities.Runtime;

namespace ioBroker_NewGen.Core.Bus
{
    /// <summary>
    /// Anwendungsspezifischer Systembus auf Basis der RSEV.Utilities-Verträge.
    /// Die eigentliche Publish-/Subscribe-Infrastruktur kommt aus der
    /// RSEV.Utilities-EventRegistry; diese Klasse liefert nur den fachlichen
    /// Bus-Vertrag für BusFrame sowie die Adressfilterung der Adapter-API.
    /// </summary>
    public sealed class SystemMessageBus : BaseMessageBus
    {
        public const string FrameEventName = "system.bus.frame";

        private readonly EventRegistry<BusFrame> _frameRegistry;
        private readonly object _subscriptionLock = new();
        private readonly Dictionary<string, List<AddressHandler>> _addressHandlers = new(StringComparer.Ordinal);
        private IRuntimeContext? _context;

        public SystemMessageBus()
            : base(
                "SystemBus",
                "RSEV.Utilities-basierter Datenbus für BusFrame-Payloads ohne Kenntnis des Adaptertyps.")
        {
            _frameRegistry = new EventRegistry<BusFrame>(FrameEventName);
        }

        /// <summary>
        /// Direkter Zugriff auf die RSEV.Utilities-Registry für typisierte
        /// Handler und Listener.
        /// </summary>
        public IEventRegistry<BusFrame> FrameRegistry => _frameRegistry;

        public int HandlerCount => _frameRegistry.GetHandlers().Count;

        public int ListenerCount => _frameRegistry.GetListeners().Count;

        public void Publish(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            _frameRegistry.Publish(this, frame);
        }

        public Task PublishAsync(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return _frameRegistry.PublishAsync(this, frame);
        }

        /// <summary>
        /// Kompatibilitäts-API für adressbezogene Subscriber. Die Ausführung
        /// wird vollständig an die RSEV.Utilities-EventRegistry delegiert.
        /// </summary>
        public void Subscribe(string address, Action<BusFrame> callback)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Adresse darf nicht leer sein.", nameof(address));
            }

            ArgumentNullException.ThrowIfNull(callback);

            var handler = new AddressHandler(address, callback, () => _context);
            lock (_subscriptionLock)
            {
                if (!_addressHandlers.TryGetValue(address, out var handlers))
                {
                    handlers = new List<AddressHandler>();
                    _addressHandlers[address] = handlers;
                }

                handlers.Add(handler);
            }

            _frameRegistry.Subscribe(handler);
        }

        public void Unsubscribe(string address, Action<BusFrame> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);

            AddressHandler? handler = null;
            lock (_subscriptionLock)
            {
                if (_addressHandlers.TryGetValue(address, out var handlers))
                {
                    var index = handlers.FindIndex(candidate => candidate.Callback == callback);
                    if (index >= 0)
                    {
                        handler = handlers[index];
                        handlers.RemoveAt(index);
                    }

                    if (handlers.Count == 0)
                    {
                        _addressHandlers.Remove(address);
                    }
                }
            }

            if (handler is not null)
            {
                _frameRegistry.Unsubscribe(handler);
            }
        }

        public void Subscribe(IEventHandler<BusFrame> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _frameRegistry.Subscribe(handler);
        }

        public void Unsubscribe(IEventHandler<BusFrame> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _frameRegistry.Unsubscribe(handler);
        }

        protected override void OnInitialize(IRuntimeContext context)
        {
            _context = context;
            context.Logger.LogInfo("[SystemBus] RSEV.Utilities-EventRegistry initialisiert.");
        }

        protected override Task OnInitializeAsync(IRuntimeContext context)
        {
            OnInitialize(context);
            return Task.CompletedTask;
        }

        protected override void OnShutdown(IRuntimeContext context)
        {
            lock (_subscriptionLock)
            {
                foreach (var handler in _addressHandlers.Values.SelectMany(static handlers => handlers))
                {
                    _frameRegistry.Unsubscribe(handler);
                }

                _addressHandlers.Clear();
            }

            context.Logger.LogInfo("[SystemBus] Shutdown.");
        }

        protected override Task OnShutdownAsync(IRuntimeContext context)
        {
            OnShutdown(context);
            return Task.CompletedTask;
        }

        private sealed class AddressHandler : BaseEventHandler<BusFrame>
        {
            private readonly string _address;
            private readonly Action<BusFrame> _callback;
            private readonly Func<IRuntimeContext?> _contextAccessor;

            public AddressHandler(string address, Action<BusFrame> callback, Func<IRuntimeContext?> contextAccessor)
            {
                _address = address;
                _callback = callback;
                _contextAccessor = contextAccessor;
            }

            public Action<BusFrame> Callback => _callback;

            protected override void OnHandle(object sender, BusFrame frame)
            {
                if (_address != "*" && !string.Equals(_address, frame.Address, StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    _callback(frame);
                }
                catch (Exception ex)
                {
                    _contextAccessor()?.Logger.LogError($"[SystemBus] Subscriberfehler für '{frame.Address}': {ex}");
                }
            }
        }
    }
}
