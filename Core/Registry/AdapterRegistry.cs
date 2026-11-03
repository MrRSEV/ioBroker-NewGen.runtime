using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ioBroker_NewGen.Core.Events;
using ioBroker_NewGen.Core.Routing;

namespace ioBroker_NewGen.Core.Registry
{
    /// <summary>
    /// Thread-sichere Registry aller bekannten Adapter (Custom + ioBroker-Legacy).
    /// Vergibt beim TCP-Bridge-Handshake eindeutige IDs im Format "Name.id".
    /// </summary>
    public sealed class AdapterRegistry
    {
        private readonly ConcurrentDictionary<string, AdapterRegistration> _adaptersById = new();
        private readonly ConcurrentDictionary<string, int> _instanceCounters = new();
        private readonly object _idLock = new();
        private readonly SystemEventBus? _eventBus;

        /// <summary>Instanzbezogene Adaptereinstellungen (Custom und Legacy).</summary>
        public ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigService Configs { get; } = new();

        /// <summary>OAuth-Anmeldungen (isolierte Browser-Sitzung) für Adapter mit Cloud-Zugang.</summary>
        public ioBroker_NewGen.Core.Adapters.Configuration.OAuthFlowService OAuth { get; }

        public AdapterRegistry(SystemEventBus? eventBus = null)
        {
            OAuth = new ioBroker_NewGen.Core.Adapters.Configuration.OAuthFlowService(Configs);
            _eventBus = eventBus;
        }

        /// <summary>
        /// Registriert einen neuen Adapter und vergibt, falls noch keine ID gesetzt ist,
        /// automatisch eine neue eindeutige ID im Format "Name.id" (ID beginnt bei 0).
        /// </summary>
        public AdapterRegistration Register(string name, AdapterType type, Dictionary<string, string>? metadata = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Adaptername darf nicht leer sein.", nameof(name));
            }

            var id = AssignNextId(name);
            var registration = new AdapterRegistration
            {
                Name = name,
                Id = id,
                Type = type,
                Metadata = metadata ?? new Dictionary<string, string>()
            };

            _adaptersById[id] = registration;
            _eventBus?.PublishAdapterLifecycle(
                this,
                new AdapterLifecycleEventArgs(registration, AdapterStatus.Created));
            return registration;
        }

        public AdapterRegistration RegisterWithId(string name, AdapterType type, string id, Dictionary<string, string>? metadata = null)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Name und ID dürfen nicht leer sein.");
            }

            var registration = new AdapterRegistration
            {
                Name = name,
                Id = id,
                Type = type,
                Metadata = metadata ?? new Dictionary<string, string>()
            };

            lock (_idLock)
            {
                if (!_adaptersById.TryAdd(id, registration))
                {
                    throw new InvalidOperationException($"Instanz '{id}' existiert bereits.");
                }

                if (int.TryParse(id[(id.LastIndexOf('.') + 1)..], out var index))
                {
                    _instanceCounters.AddOrUpdate(name, index, (_, current) => Math.Max(current, index));
                }
            }

            _eventBus?.PublishAdapterLifecycle(
                this,
                new AdapterLifecycleEventArgs(registration, AdapterStatus.Created));
            return registration;
        }

        /// <summary>
        /// Vergibt die n�chste freie Instanz-ID f�r den �bergebenen Adapternamen
        /// (Format "Name.id", z. B. "mqtt.0", "mqtt.1", ...).
        /// </summary>
        public string AssignNextId(string name)
        {
            lock (_idLock)
            {
                var nextInstance = _instanceCounters.AddOrUpdate(name, 0, (_, current) => current + 1);
                return $"{name}.{nextInstance}";
            }
        }

        /// <summary>
        /// Liefert den registrierten Adapter zur �bergebenen ID, oder <c>null</c>, falls unbekannt.
        /// </summary>
        public AdapterRegistration? Get(string id)
        {
            return _adaptersById.TryGetValue(id, out var registration) ? registration : null;
        }

        /// <summary>
        /// Pr�ft, ob ein Adapter mit der �bergebenen ID existiert.
        /// </summary>
        public bool Exists(string id) => _adaptersById.ContainsKey(id);

        /// <summary>
        /// Liefert alle registrierten Adapter.
        /// </summary>
        public IReadOnlyCollection<AdapterRegistration> GetAll() => _adaptersById.Values.ToList();

        /// <summary>
        /// Liefert alle registrierten Adapter eines bestimmten Typs.
        /// </summary>
        public IReadOnlyCollection<AdapterRegistration> GetByType(AdapterType type)
        {
            return _adaptersById.Values.Where(a => a.Type == type).ToList();
        }

        /// <summary>
        /// Liefert eine noch nicht an einen TCP-Endpunkt gebundene Instanz mit
        /// dem angegebenen Namen. Damit kann ein von NodeAdapterRuntime
        /// vorregistrierter Prozess beim Bridge-Handshake dieselbe Name.id-ID
        /// weiterverwenden.
        /// </summary>
        public AdapterRegistration? FindUnbound(string name, AdapterType type)
        {
            return _adaptersById.Values
                .Where(adapter => adapter.Type == type &&
                                  string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                  string.IsNullOrWhiteSpace(adapter.TcpEndpoint))
                .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>
        /// Entfernt einen Adapter aus der Registry (z. B. nach Verbindungsabbruch).
        /// </summary>
        public bool Unregister(string id)
        {
            return _adaptersById.TryRemove(id, out _);
        }

        /// <summary>
        /// Aktualisiert Status und optional den Zeitpunkt des letzten Lebenszeichens eines Adapters.
        /// </summary>
        public void UpdateStatus(string id, AdapterStatus status, bool touchLastSeen = true)
        {
            if (_adaptersById.TryGetValue(id, out var registration))
            {
                var previousStatus = registration.Status;
                registration.Status = status;
                if (touchLastSeen)
                {
                    registration.LastSeenAt = DateTime.UtcNow;
                }

                if (previousStatus != status)
                {
                    var now = DateTime.UtcNow;
                    registration.LastStatusChangeAt = now;
                    switch (status)
                    {
                        case AdapterStatus.Connected:
                            registration.StartedAt = now;
                            registration.StartCount++;
                            break;
                        case AdapterStatus.Disconnected:
                            registration.StoppedAt = now;
                            break;
                        case AdapterStatus.Faulted:
                            registration.StoppedAt = now;
                            registration.FaultCount++;
                            break;
                    }

                    _eventBus?.PublishAdapterLifecycle(
                        this,
                        new AdapterLifecycleEventArgs(registration, previousStatus));
                }
            }
        }

        /// <summary>Merkt den letzten Fehler eines Adapters für die Metrikanzeige.</summary>
        public void RecordError(string id, string message)
        {
            if (_adaptersById.TryGetValue(id, out var registration))
            {
                registration.LastError = message;
                registration.LastErrorAt = DateTime.UtcNow;
            }
        }

        /// <summary>Zählt einen Neustart eines Adapters.</summary>
        public void RecordRestart(string id)
        {
            if (_adaptersById.TryGetValue(id, out var registration))
            {
                registration.RestartCount++;
            }
        }
    }
}
