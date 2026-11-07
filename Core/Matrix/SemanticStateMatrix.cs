using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Events;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Zentrale Engine für Objekte, States, Semantik, History und Persistenz.
    /// Der semantische Vektor gehört zum MatrixObject; State-Werte und History
    /// bleiben vom Embedding-Lebenszyklus getrennt.
    /// Thread-sicher über einen zentralen <see cref="ReaderWriterLockSlim"/>
    /// für Matrix-/Index-Konsistenz sowie interne Locks in <see cref="StateNode"/>.
    /// </summary>
    public sealed class SemanticStateMatrix : IDisposable
    {
        private readonly ConcurrentDictionary<string, MatrixObject> _objects = new();
        private readonly ConcurrentDictionary<string, StateNode> _states = new();
        private readonly ConcurrentQueue<string> _semanticReindexQueue = new();
        private readonly ConcurrentDictionary<string, List<Action<StateNode>>> _subscriptions = new();
        private readonly object _subscriptionLock = new();
        private readonly ReaderWriterLockSlim _matrixLock = new(LockRecursionPolicy.NoRecursion);

        private readonly IRoleService _roleService;
        private readonly IEmbeddingService _embeddingService;
        private readonly IVectorIndex _semanticIndex;
        private readonly string _snapshotPath;
        private readonly int _maxInMemoryHistory;
        private readonly SystemEventBus? _eventBus;

        private Timer? _cronTimer;

        /// <summary>
        /// Wird nach jedem erfolgreichen State-Update ausgelöst. Der Event ist
        /// bewusst unabhängig vom semantischen Worker und dient unter anderem
        /// der Legacy-Adapter-Subscription-API.
        /// </summary>
        public event Action<StateChangedEventArgs>? StateChanged;

        /// <summary>Wird bei Registrierung, Änderung oder Entfernung eines Objekts ausgelöst.</summary>
        public event Action<MatrixObjectChangedEventArgs>? ObjectChanged;

        /// <summary>
        /// Erstellt eine neue <see cref="SemanticStateMatrix"/>.
        /// </summary>
        /// <param name="snapshotPath">Pfad zur JSONL-Snapshot-Datei.</param>
        /// <param name="roleService">Rollen-/Berechtigungsdienst. Standard: <see cref="DefaultRoleService"/> (voller Zugriff).</param>
        /// <param name="embeddingService">Embedding-Dienst. Standard: <see cref="HashingEmbeddingService"/>.</param>
        /// <param name="semanticIndex">Semantischer Index. Standard: <see cref="VectorIndex"/> (Brute-Force-Kosinus).</param>
        public SemanticStateMatrix(
            string snapshotPath,
            IRoleService? roleService = null,
            IEmbeddingService? embeddingService = null,
            IVectorIndex? semanticIndex = null,
            int maxInMemoryHistory = 500,
            SystemEventBus? eventBus = null)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                throw new ArgumentException("Snapshot-Pfad darf nicht leer sein.", nameof(snapshotPath));
            }

            _snapshotPath = snapshotPath;
            _roleService = roleService ?? new DefaultRoleService();
            _embeddingService = embeddingService ?? new HashingEmbeddingService();
            _semanticIndex = semanticIndex ?? new VectorIndex();
            _maxInMemoryHistory = maxInMemoryHistory > 0
                ? maxInMemoryHistory
                : throw new ArgumentOutOfRangeException(nameof(maxInMemoryHistory));
            _eventBus = eventBus;
        }

        /// <summary>
        /// Liefert den für diese Matrix verwendeten Rollen-/Berechtigungsdienst.
        /// Der Admin-Adapter kann darüber Benutzer- und Rollenrechte verwalten,
        /// ohne die interne Matrixstruktur zu umgehen.
        /// </summary>
        public IRoleService RoleService => _roleService;

        /// <summary>Sprachweiche des Hosts (gesetzt beim Start); steuert Sprache für Namen und Werte.</summary>
        public ioBroker_NewGen.Core.Localization.ILocalizationService? Localization { get; set; }

        /// <summary>Aktive Sprache (z. B. "de_DE") für semantische Verknüpfung von Payloads und Werten.</summary>
        public string Language => Localization?.Language ?? "de_DE";

        /// <summary>
        /// Liefert den State-Wert sprachabhängig: Booleans über <c>value.true</c>/<c>value.false</c>,
        /// Zahlen und Datum in der Kultur der aktiven Sprache.
        /// </summary>
        public string? GetLocalizedValue(string id)
        {
            var value = GetState(id)?.Value;
            var loc = Localization;
            return value switch
            {
                null => null,
                bool b when loc is not null => loc.Translate(b ? "value.true" : "value.false"),
                IFormattable f when loc is not null => f.ToString(null, loc.Culture),
                _ => value.ToString()
            };
        }

        /// <summary>
        /// Registriert ein Objekt und markiert dessen Objekt-Embedding für die
        /// Hintergrundverarbeitung. Der Register-Pfad führt keine Embedding-
        /// Generierung synchron aus.
        /// </summary>
        public bool RegisterObject(MatrixObject matrixObject)
        {
            ArgumentNullException.ThrowIfNull(matrixObject);

            if (matrixObject.StateDefinition is not null &&
                !matrixObject.StateDefinition.ValidateDefinition(out _))
            {
                return false;
            }

            var registered = false;
            _matrixLock.EnterWriteLock();
            try
            {
                if (!_objects.TryAdd(matrixObject.Id, matrixObject))
                {
                    return false;
                }

                var state = _states.GetOrAdd(matrixObject.Id, id => new StateNode(id, _maxInMemoryHistory));
                foreach (var kvp in matrixObject.Meta)
                {
                    state.Meta[kvp.Key] = kvp.Value;
                }

                MarkSemanticDirtyLocked(matrixObject);
                registered = true;
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }

            if (registered)
            {
                PublishObjectChanged(new MatrixObjectChangedEventArgs(matrixObject.Id, matrixObject));
            }

            return registered;
        }

        /// <summary>
        /// Aktualisiert semantische Objektinformationen. Änderungen an Name,
        /// Typ, Metadaten oder StateDefinition lösen einen Reindex aus.
        /// </summary>
        public bool UpdateObject(MatrixObject matrixObject)
        {
            ArgumentNullException.ThrowIfNull(matrixObject);

            if (matrixObject.StateDefinition is not null &&
                !matrixObject.StateDefinition.ValidateDefinition(out _))
            {
                return false;
            }

            MatrixObject? published = null;
            var updatedResult = false;
            _matrixLock.EnterWriteLock();
            try
            {
                if (!_objects.TryGetValue(matrixObject.Id, out var current))
                {
                    return false;
                }

                var semanticChanged = !string.Equals(current.Name, matrixObject.Name, StringComparison.Ordinal) ||
                    !string.Equals(current.Type, matrixObject.Type, StringComparison.Ordinal) ||
                    !MetaEquals(current.Meta, matrixObject.Meta) ||
                    !Equals(current.StateDefinition, matrixObject.StateDefinition);

                var updated = new MatrixObject
                {
                    Id = matrixObject.Id,
                    Name = matrixObject.Name,
                    Type = matrixObject.Type,
                    Meta = new Dictionary<string, string>(matrixObject.Meta),
                    StateDefinition = matrixObject.StateDefinition,
                    Embedding = current.Embedding,
                    LastEmbeddingUpdate = current.LastEmbeddingUpdate,
                    SemanticDirty = current.SemanticDirty,
                    SemanticVersion = current.SemanticVersion
                };

                _objects[matrixObject.Id] = updated;
                var state = _states.GetOrAdd(matrixObject.Id, id => new StateNode(id, _maxInMemoryHistory));
                state.Meta.Clear();
                foreach (var kvp in updated.Meta)
                {
                    state.Meta[kvp.Key] = kvp.Value;
                }

                if (semanticChanged)
                {
                    MarkSemanticDirtyLocked(updated);
                }
                published = updated;
                updatedResult = true;
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }

            if (updatedResult && published is not null)
            {
                PublishObjectChanged(new MatrixObjectChangedEventArgs(matrixObject.Id, published));
            }

            return updatedResult;
        }

        /// <summary>Fordert eine asynchrone semantische Neuindizierung an.</summary>
        public bool RequestSemanticReindex(string id)
        {
            _matrixLock.EnterWriteLock();
            try
            {
                if (!_objects.TryGetValue(id, out var matrixObject))
                {
                    return false;
                }

                MarkSemanticDirtyLocked(matrixObject);
                return true;
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }
        }

        /// <summary>Alias für explizite Reindex-Aufrufer.</summary>
        public bool ReindexObject(string id) => RequestSemanticReindex(id);

        /// <summary>
        /// Liefert das registrierte Objekt zur �bergebenen ID, oder <c>null</c>, falls unbekannt.
        /// </summary>
        public MatrixObject? GetObject(string id)
        {
            _matrixLock.EnterReadLock();
            try
            {
                return _objects.TryGetValue(id, out var matrixObject) ? matrixObject : null;
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }
        }

        public IReadOnlyDictionary<string, MatrixObject> GetObjects(string pattern = "*")
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return new Dictionary<string, MatrixObject>(StringComparer.Ordinal);
            }

            var regex = new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            _matrixLock.EnterReadLock();
            try
            {
                return _objects
                    .Where(entry => regex.IsMatch(entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Entfernt ein Objekt, den zugehörigen State und den Indexeintrag.
        /// </summary>
        public bool RemoveObject(string id)
        {
            var removed = false;
            _matrixLock.EnterWriteLock();
            try
            {
                var removedObject = _objects.TryRemove(id, out _);
                var removedState = _states.TryRemove(id, out _);
                _semanticIndex.Remove(id);

                lock (_subscriptionLock)
                {
                    _subscriptions.TryRemove(id, out _);
                }

                removed = removedObject || removedState;
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }

            if (removed)
            {
                PublishObjectChanged(new MatrixObjectChangedEventArgs(id, null));
            }

            return removed;
        }

        /// <summary>
        /// Liefert den aktuellen Zustand (State) zur �bergebenen ID, oder <c>null</c>, falls unbekannt.
        /// </summary>
        public StateNode? GetState(string id)
        {
            _matrixLock.EnterReadLock();
            try
            {
                return _states.TryGetValue(id, out var state) ? state : null;
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Liefert einen State nur, wenn der Benutzer Leserechte besitzt.
        /// </summary>
        public StateNode? GetState(string id, string user)
        {
            return _roleService.CanRead(user, id) ? GetState(id) : null;
        }

        /// <summary>
        /// Liefert bekannte States, deren ID auf das einfache ioBroker-Globmuster
        /// passt. Die Rückgabe enthält nur Referenzen auf thread-sichere
        /// <see cref="StateNode"/>-Instanzen.
        /// </summary>
        public IReadOnlyDictionary<string, StateNode> GetStates(string pattern = "*")
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return new Dictionary<string, StateNode>(StringComparer.Ordinal);
            }

            var regex = new Regex(
                "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            _matrixLock.EnterReadLock();
            try
            {
                return _states
                    .Where(entry => regex.IsMatch(entry.Key) && _roleService.CanRead("system", entry.Key))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }
        }

        public bool SetPermissions(string user, string stateId, bool canRead, bool canWrite)
        {
            if (_roleService is not IRoleManagementService roleManagementService)
            {
                return false;
            }

            roleManagementService.SetPermissions(user, stateId, canRead, canWrite);
            return true;
        }

        /// <summary>
        /// Setzt den Wert eines States. Validiert zuvor die Schreibrechte des
        /// übergebenen Benutzers, schreibt Wert und History und benachrichtigt
        /// Subscriber. Embeddings und der Vektorindex werden hier bewusst nicht
        /// angefasst.
        /// </summary>
        /// <returns><c>true</c> bei Erfolg, <c>false</c> wenn der Benutzer keine Schreibrechte besitzt.</returns>
        public bool SetState(string id, object? value, string user = "system")
        {
            if (!_roleService.CanWrite(user, id))
            {
                return false;
            }

            StateNode state;

            _matrixLock.EnterWriteLock();
            try
            {
                if (!_objects.TryGetValue(id, out var matrixObject))
                {
                    matrixObject = new MatrixObject
                    {
                        Id = id,
                        Name = id,
                        Type = "state"
                    };
                    _objects.TryAdd(id, matrixObject);
                    MarkSemanticDirtyLocked(matrixObject);
                }

                if (matrixObject.StateDefinition is not null &&
                    !matrixObject.StateDefinition.Validate(value, out _))
                {
                    return false;
                }

                state = _states.GetOrAdd(id, key => new StateNode(key, _maxInMemoryHistory));
                state.Update(value, user);
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }

            NotifySubscribers(id, state);
            var changed = new StateChangedEventArgs(id, state, user);
            _eventBus?.PublishStateChanged(this, changed);
            PublishStateChanged(changed);

            return true;
        }

        /// <summary>
        /// Übernimmt einen StateUpdate-Frame vom Systembus in die Matrix.
        /// Der Bus bleibt dabei neutral; nur die Matrix interpretiert den
        /// Payload anhand des ValueType-Feldes.
        /// </summary>
        public bool ApplyFrame(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);

            if (frame.Kind != BusFrameKind.StateUpdate || string.IsNullOrWhiteSpace(frame.Address))
            {
                return false;
            }

            object? value;
            try
            {
                value = DecodePayload(frame.Payload, frame.ValueType);
            }
            catch
            {
                return false;
            }

            if (!_roleService.CanWrite(frame.AdapterId, frame.Address))
            {
                return false;
            }

            StateNode state;

            _matrixLock.EnterWriteLock();
            try
            {
                if (!_objects.TryGetValue(frame.Address, out var matrixObject))
                {
                    matrixObject = new MatrixObject
                    {
                        Id = frame.Address,
                        Name = frame.Address,
                        Type = "state"
                    };
                    _objects.TryAdd(frame.Address, matrixObject);
                    MarkSemanticDirtyLocked(matrixObject);
                }

                if (matrixObject.StateDefinition is not null &&
                    !matrixObject.StateDefinition.Validate(value, out _))
                {
                    return false;
                }

                state = _states.GetOrAdd(frame.Address, id => new StateNode(id, _maxInMemoryHistory));
                state.Update(value, frame.AdapterId, frame.Timestamp == default ? null : frame.Timestamp);
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }

            NotifySubscribers(frame.Address, state);
            var changed = new StateChangedEventArgs(frame.Address, state, frame.AdapterId);
            _eventBus?.PublishStateChanged(this, changed);
            PublishStateChanged(changed);
            return true;
        }

        private void PublishStateChanged(StateChangedEventArgs changed)
        {
            var handlers = StateChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList().Cast<Action<StateChangedEventArgs>>())
            {
                try
                {
                    handler(changed);
                }
                catch
                {
                    // Ein externer Subscriber darf den State-Schreibpfad nicht
                    // destabilisieren.
                }
            }
        }

        private void PublishObjectChanged(MatrixObjectChangedEventArgs changed)
        {
            var handlers = ObjectChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList().Cast<Action<MatrixObjectChangedEventArgs>>())
            {
                try
                {
                    handler(changed);
                }
                catch
                {
                    // Ein externer Subscriber darf Objektoperationen nicht
                    // destabilisieren.
                }
            }
        }

        /// <summary>
        /// Verarbeitet dirty markierte MatrixObjects. Der Embedding-Dienst wird
        /// außerhalb des Matrix-Locks aufgerufen; eine gleichzeitig eintreffende
        /// Metadatenänderung gewinnt und lässt das Objekt dirty für den nächsten
        /// Worker-Lauf.
        /// </summary>
        public int ProcessDirtySemanticObjects(CancellationToken cancellationToken = default)
        {
            while (_semanticReindexQueue.TryDequeue(out _))
            {
            }

            var candidates = new List<(string Id, string Source, long Version)>();
            _matrixLock.EnterReadLock();
            try
            {
                foreach (var matrixObject in _objects.Values)
                {
                    if (matrixObject.SemanticDirty)
                    {
                        candidates.Add((
                            matrixObject.Id,
                            matrixObject.BuildEmbeddingSourceText(),
                            matrixObject.SemanticVersion));
                    }
                }
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }

            var processed = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                float[] embedding;
                try
                {
                    embedding = _embeddingService.Encode(candidate.Source);
                }
                catch
                {
                    // Das Objekt bleibt dirty und wird im nächsten Lauf erneut versucht.
                    continue;
                }

                _matrixLock.EnterWriteLock();
                try
                {
                    if (_objects.TryGetValue(candidate.Id, out var matrixObject) &&
                        matrixObject.SemanticDirty &&
                        matrixObject.SemanticVersion == candidate.Version)
                    {
                        matrixObject.StoreEmbedding(embedding, DateTime.UtcNow);
                        _semanticIndex.Add(candidate.Id, matrixObject.Embedding);
                        processed++;
                    }
                }
                finally
                {
                    _matrixLock.ExitWriteLock();
                }
            }

            return processed;
        }

        /// <summary>
        /// Baut den Vektorindex aus den aktuell gespeicherten Objekt-Embeddings
        /// vollständig neu auf. Dirty Objekte ohne fertiges Embedding werden
        /// bewusst ausgelassen und vom Worker nachbearbeitet.
        /// </summary>
        public int RebuildVectorIndex()
        {
            _matrixLock.EnterWriteLock();
            try
            {
                foreach (var id in _objects.Keys)
                {
                    _semanticIndex.Remove(id);
                }

                var indexed = 0;
                foreach (var matrixObject in _objects.Values)
                {
                    if (matrixObject.Embedding.Length == 0)
                    {
                        continue;
                    }

                    _semanticIndex.Add(matrixObject.Id, matrixObject.Embedding);
                    indexed++;
                }

                return indexed;
            }
            finally
            {
                _matrixLock.ExitWriteLock();
            }
        }

        private void MarkSemanticDirtyLocked(MatrixObject matrixObject)
        {
            matrixObject.MarkSemanticDirty();
            _semanticReindexQueue.Enqueue(matrixObject.Id);
        }

        private static bool MetaEquals(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var value) ||
                    !string.Equals(entry.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Registriert einen Callback, der bei jeder �nderung des Zustands mit
        /// der �bergebenen ID aufgerufen wird.
        /// </summary>
        public void Subscribe(string id, Action<StateNode> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);

            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(id, out var callbacks))
                {
                    callbacks = new List<Action<StateNode>>();
                    _subscriptions[id] = callbacks;
                }

                callbacks.Add(callback);
            }
        }

        /// <summary>
        /// Entfernt einen zuvor registrierten Subscription-Callback.
        /// </summary>
        public void Unsubscribe(string id, Action<StateNode> callback)
        {
            lock (_subscriptionLock)
            {
                if (_subscriptions.TryGetValue(id, out var callbacks))
                {
                    callbacks.Remove(callback);
                }
            }
        }

        private void NotifySubscribers(string id, StateNode state)
        {
            List<Action<StateNode>>? callbacks;
            lock (_subscriptionLock)
            {
                if (!_subscriptions.TryGetValue(id, out var existing))
                {
                    return;
                }

                callbacks = existing.ToList();
            }

            foreach (var callback in callbacks)
            {
                try
                {
                    callback(state);
                }
                catch
                {
                    // Subscriber-Fehler d�rfen die Matrix nicht destabilisieren.
                }
            }
        }

        /// <summary>
        /// F�hrt eine semantische Suche �ber alle registrierten States anhand
        /// eines Freitextes durch (z. B. "Temperatur Wohnzimmer").
        /// </summary>
        public List<StateNode> SemanticQuery(string text, int k = 10)
        {
            return SemanticQuery(text, k, "system");
        }

        public List<StateNode> SemanticQuery(string text, int k, string user)
        {
            var queryEmbedding = _embeddingService.Encode(text);
            var stateNodes = new List<StateNode>();
            _matrixLock.EnterReadLock();
            try
            {
                var results = _semanticIndex.Search(queryEmbedding, k);
                foreach (var (id, _) in results)
                {
                    if (_roleService.CanRead(user, id) && _states.TryGetValue(id, out var state))
                    {
                        stateNodes.Add(state);
                    }
                }
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }

            return stateNodes;
        }

        /// <summary>
        /// Liefert die History-Samples eines States, optional gefiltert nach
        /// einem Zeitraum. Wird z. B. von einem history.0-kompatiblen Adapter genutzt.
        /// </summary>
        public IEnumerable<HistorySample> GetHistory(string id, DateTime? from = null, DateTime? to = null)
        {
            return GetHistory(id, from, to, "system");
        }

        public IEnumerable<HistorySample> GetHistory(
            string id,
            DateTime? from,
            DateTime? to,
            string user)
        {
            if (!_roleService.CanRead(user, id))
            {
                return Array.Empty<HistorySample>();
            }

            List<HistorySample> samples;
            _matrixLock.EnterReadLock();
            try
            {
                if (!_states.TryGetValue(id, out var state))
                {
                    return Array.Empty<HistorySample>();
                }

                samples = state.History.ToList();
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }

            if (from.HasValue)
            {
                samples = samples.Where(sample => sample.Timestamp >= from.Value).ToList();
            }

            if (to.HasValue)
            {
                samples = samples.Where(sample => sample.Timestamp <= to.Value).ToList();
            }

            return samples.OrderBy(sample => sample.Timestamp).ToList();
        }

        /// <summary>
        /// Gibt den aktuellen Objektbaum (Objekte + aktuelle State-Werte) als JSON aus.
        /// </summary>
        public string DisplayAsJson()
        {
            return DisplayAsJson("system");
        }

        public string DisplayAsJson(string user)
        {
            _matrixLock.EnterReadLock();
            try
            {
                var tree = _objects.Values
                    .OrderBy(obj => obj.Id, StringComparer.Ordinal)
                    .Where(obj => _roleService.CanRead(user, obj.Id))
                    .Select(obj => new
                    {
                        obj.Id,
                        obj.Name,
                        obj.Type,
                        Meta = obj.Meta
                            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                        obj.StateDefinition,
                        State = _states.TryGetValue(obj.Id, out var state)
                            ? new { state.Value, state.Timestamp, state.LastWrittenBy }
                            : null
                    });

                return JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true });
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Lädt die Snapshot-Datei (sofern vorhanden), rekonstruiert States
        /// und History sowie die semantischen Objekt-Embeddings. Alte
        /// History-Embeddings werden absichtlich nicht übernommen.
        /// Wird typischerweise im OnStart-Lifecycle-Hook aufgerufen.
        /// </summary>
        public async Task LoadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_snapshotPath))
            {
                return;
            }

            await foreach (var line in ReadLinesAsync(_snapshotPath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                SnapshotEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<SnapshotEntry>(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry is null || string.IsNullOrEmpty(entry.Id))
                {
                    continue;
                }

                _matrixLock.EnterWriteLock();
                try
                {
                    if (string.Equals(entry.Kind, "object", StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreObjectSnapshotLocked(entry);
                        continue;
                    }

                    var state = _states.GetOrAdd(entry.Id, id => new StateNode(id, _maxInMemoryHistory));
                    var sample = new HistorySample(entry.Id, entry.Timestamp, NormalizeValue(entry.Value));
                    state.AppendHistorySample(sample);
                    state.Restore(sample.Value, sample.Timestamp, writtenBy: null);

                    if (!_objects.ContainsKey(entry.Id))
                    {
                        var matrixObject = new MatrixObject
                        {
                            Id = entry.Id,
                            Name = entry.Name ?? entry.Id,
                            Type = entry.Type ?? "state"
                        };
                        _objects.TryAdd(entry.Id, matrixObject);
                        MarkSemanticDirtyLocked(matrixObject);
                    }
                }
                finally
                {
                    _matrixLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Persistiert semantische Objekt-Daten und alle History-Samples als
        /// JSONL-Datei. History-Zeilen enthalten kein Embedding.
        /// Wird typischerweise im OnStop-Lifecycle-Hook sowie periodisch
        /// (<see cref="StartCronSnapshot"/>) aufgerufen.
        /// </summary>
        public async Task WriteSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var directory = Path.GetDirectoryName(_snapshotPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<SnapshotEntry> entries;
            _matrixLock.EnterReadLock();
            try
            {
                entries = new List<SnapshotEntry>();
                foreach (var matrixObject in _objects.Values.OrderBy(obj => obj.Id, StringComparer.Ordinal))
                {
                    entries.Add(new SnapshotEntry
                    {
                        Kind = "object",
                        Id = matrixObject.Id,
                        Name = matrixObject.Name,
                        Type = matrixObject.Type,
                        Meta = new Dictionary<string, string>(matrixObject.Meta),
                        StateDefinition = matrixObject.StateDefinition,
                        Embedding = matrixObject.Embedding,
                        LastEmbeddingUpdate = matrixObject.LastEmbeddingUpdate,
                        SemanticDirty = matrixObject.SemanticDirty
                    });
                }

                foreach (var state in _states.Values.OrderBy(state => state.Id, StringComparer.Ordinal))
                {
                    foreach (var sample in state.History.OrderBy(sample => sample.Timestamp))
                    {
                        entries.Add(new SnapshotEntry
                        {
                            Kind = "history",
                            Id = sample.Id,
                            Timestamp = sample.Timestamp,
                            Value = sample.Value
                        });
                    }
                }
            }
            finally
            {
                _matrixLock.ExitReadLock();
            }

            await using var writer = new StreamWriter(_snapshotPath, append: false);
            foreach (var entry in entries)
            {
                var json = JsonSerializer.Serialize(entry);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            }
        }

        /// <summary>
        /// Startet einen periodischen Timer, der alle <paramref name="interval"/>
        /// ein Abbild der History-Samples auf die Festplatte schreibt.
        /// </summary>
        public void StartCronSnapshot(TimeSpan interval)
        {
            _cronTimer?.Dispose();
            _cronTimer = new Timer(
                _ => WriteSnapshotAsync().GetAwaiter().GetResult(),
                state: null,
                dueTime: interval,
                period: interval);
        }

        /// <summary>
        /// Stoppt den periodischen Snapshot-Timer.
        /// </summary>
        public void StopCronSnapshot()
        {
            _cronTimer?.Dispose();
            _cronTimer = null;
        }

        public void Dispose()
        {
            StopCronSnapshot();
            _matrixLock.Dispose();
        }

        private void RestoreObjectSnapshotLocked(SnapshotEntry entry)
        {
            var current = _objects.GetValueOrDefault(entry.Id);
            var matrixObject = new MatrixObject
            {
                Id = entry.Id,
                Name = entry.Name ?? current?.Name ?? entry.Id,
                Type = entry.Type ?? current?.Type ?? "state",
                Meta = entry.Meta is null
                    ? new Dictionary<string, string>(current?.Meta ?? new Dictionary<string, string>())
                    : new Dictionary<string, string>(entry.Meta),
                StateDefinition = entry.StateDefinition ?? current?.StateDefinition,
                Embedding = entry.Embedding?.ToArray() ?? Array.Empty<float>(),
                LastEmbeddingUpdate = entry.LastEmbeddingUpdate ?? DateTime.MinValue,
                SemanticDirty = entry.SemanticDirty ?? true,
                SemanticVersion = current?.SemanticVersion ?? 1
            };

            _objects[entry.Id] = matrixObject;
            var state = _states.GetOrAdd(entry.Id, id => new StateNode(id, _maxInMemoryHistory));
            state.Meta.Clear();
            foreach (var kvp in matrixObject.Meta)
            {
                state.Meta[kvp.Key] = kvp.Value;
            }

            if (matrixObject.SemanticDirty)
            {
                _semanticReindexQueue.Enqueue(matrixObject.Id);
            }
            else if (matrixObject.Embedding.Length > 0)
            {
                _semanticIndex.Add(matrixObject.Id, matrixObject.Embedding);
            }
        }

        private static object? NormalizeValue(object? value)
        {
            if (value is JsonElement jsonElement)
            {
                return jsonElement.ValueKind switch
                {
                    JsonValueKind.String => jsonElement.GetString(),
                    JsonValueKind.Number => jsonElement.TryGetInt64(out var longValue) ? longValue : jsonElement.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => jsonElement.GetRawText()
                };
            }

            return value;
        }

        private static object? DecodePayload(ReadOnlyMemory<byte> payload, string valueType)
        {
            if (payload.IsEmpty)
            {
                return null;
            }

            var normalizedType = (valueType ?? string.Empty).Trim().ToLowerInvariant();
            var text = Encoding.UTF8.GetString(payload.Span);

            return normalizedType switch
            {
                "bool" or "boolean" => bool.Parse(text),
                "byte" => byte.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "short" => short.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "int" or "int32" or "integer" => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "long" or "int64" => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "float" or "single" => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "double" or "decimal" or "number" => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
                "string" or "text" => text,
                "json" => DecodeJson(text),
                _ => payload.ToArray()
            };
        }

        private static object? DecodeJson(string text)
        {
            using var document = JsonDocument.Parse(text);
            return NormalizeValue(document.RootElement.Clone());
        }

        private static async IAsyncEnumerable<string> ReadLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                yield return line;
            }
        }
    }
}
