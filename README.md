# ioBroker-NewGen.core

> Core application of **ioBroker-NewGen**. Project vision, goals and overall architecture: see the [repository README](../README.md).  
> Current status: **dev0.4.1**.

The core hosts the Matrix, state-sync engine, routing, security and the adapter runtime. It uses **RSEV.Utilities 3.0.0.0** as its lifecycle, messaging, event, process and assembly-loading infrastructure. AdminUI and all native adapters run as separate external assemblies under `Adapters/custom`.

Every adapter start, stop and error state (custom and legacy) is written to the log. The admin dashboard is served on the configured `admin.adminPort` (default `8080`) at `/`, `/index.html` and `/admin`.

The drop-in `@iobroker/adapter-core` replacement lives in `Legacy/AdapterCore`.

---
# Neuronal Storage (Semantic Vector Matrix)

The **Matrix** is not an object.  
It is a **semantic vector space** — a neuronal storage layer that holds embeddings for objects, states, and history samples.

All visible structures (ObjectTree, StateNodes, History) are **projections** or **materialized views** derived from this vector space.

## Matrix

### Description
Central semantic vector matrix for objects, states, embeddings, history, and persistence.  
It acts as a high‑dimensional storage layer without any intelligence.

### Components
- **objects** — materialized object views  
- **states** — materialized state views  
- **subscriptions** — callbacks for state changes  
- **roleService** — permission validation  
- **embeddingService** — text/payload embedding generator  
- **semanticIndex** — vector index (e.g., HNSW)  
- **snapshotPath** — path to JSONL snapshot file

### Lifecycle
- **OnStart**  
  Loads snapshot, reconstructs history samples, rebuilds embeddings, repopulates semantic index.
- **OnStop**  
  Writes a complete snapshot of all history samples.
- **CronSnapshot**  
  Periodically writes incremental snapshots.

### API
- **RegisterObject(MatrixObject) -> bool**  
  Generates object embedding, inserts into semantic index, creates a StateNode view.

- **GetObject(string id) -> MatrixObject**  
  Returns the materialized object view.

- **GetState(string id) -> StateNode**  
  Returns the materialized state view.

- **SetState(string id, object value, string user) -> bool**  
  Validates permissions, generates payload embedding, updates state view, appends history sample, updates semantic index, triggers subscriptions.

- **Subscribe(string id, Action<StateNode>)**  
  Registers a callback for state changes.

- **SemanticQuery(string text, int k) -> List<StateNode>**  
  Encodes text into an embedding and performs k‑NN search.

- **GetHistory(string id, DateTime? from, DateTime? to)**  
  Returns history samples for the history.0 adapter.

- **DisplayAsJson() -> string**  
  Outputs the materialized object tree as JSON.

- **WriteSnapshot()**  
  Persists all history samples as JSONL.

---

# Core Data Structures

## MatrixObject

### Description
Represents a device, channel, or datapoint.  
It is a **materialized projection** of embeddings stored in the Matrix.

### Fields
- **Id** — string  
- **Name** — string  
- **Type** — string  
- **Meta** — dictionary of metadata

### Embedding
- **source**: `Id + Name + Type + Meta`  
- **usage**: semantic identity of the object

---

## StateNode

### Description
Materialized view of the current state of an object.

### Fields
- **Id** — string  
- **Value** — object  
- **Timestamp** — DateTime  
- **Embedding** — float[]  
- **Meta** — metadata dictionary  
- **History** — queue of history samples

### Responsibilities
- Holds current value  
- Holds semantic payload embedding  
- Holds history samples  
- Is indexed in the vector index

---

## HistorySample

### Description
Represents a historical state value with timestamp and embedding.

### Fields
- **Id** — string  
- **Timestamp** — DateTime  
- **Value** — object  
- **Embedding** — float[]

### Usage
- Persistent time series  
- Semantic analysis over time  
- Reconstruction of historical states  
- Clustering, trends, anomaly detection

---

## VectorIndex

### Description
Semantic index for embeddings (e.g., HNSW).

### API
- **Add** — adds a state node embedding  
- **Search** — k‑NN search

---

## IEmbeddingService

### Description
Generates embeddings from text or payload.

### API
- **Encode(string) -> float[]**

---

## IRoleService

### Description
Role and permission validation.

### API
- **CanWrite(user, stateId) -> bool**  
- **CanRead(user, stateId) -> bool**

`DefaultRoleService` additionally supports persistent user-specific permissions,
roles and role assignments in `roles.json`; the external AdminUI manages these
through `/api/permissions`, `/api/roles` and `/api/users`.

---

# Adapter Configuration

Every adapter instance (`hue.0`, `mqtt.0`, legacy `foo.0`, …) has its own settings (like `native` in the original ioBroker),
stored in `data/adapter-config/<instanceId>.json` and editable in the Admin UI under *Adapter → Konfiguration*
(permission `SystemConfigure`).

- **Schema:** defaults plus an optional jsonConfig (`AdapterConfigSchema`). Custom adapters override `IAdapter.ConfigSchema`
  (e.g. `BuildSchema(TextField(...), NumberField(...), PasswordField(...))` in `DeviceAdapterBase`) or ship `io-package.json` /
  `admin/jsonConfig.json` next to the DLL. Legacy adapters provide the same files in their directory.
- **Custom adapters:** `Config.Get<T>("hue.bridgeUri")` reads the instance value first and falls back to the global `config.toml`.
  Values are a snapshot taken at adapter start; saving restarts a running adapter.
- **Legacy adapters:** the host passes `--configFile <path>`; the AdapterCore exposes it as `this.config`. A restart is required after saving.
- **API:** `POST /api/adapters/config` with `{adapterId, action: "get" | "save", values}`. Unknown keys and wrong types are rejected,
  `password` fields are masked and an empty/masked value keeps the stored secret.
- **jsonConfig support:** panel, tabs, text, number, checkbox, select, password, textarea, header, staticText, divider and `hidden`/`disabled`
  conditions. Other types (`table`, `sendTo`, `custom`, …) are not rendered yet.
- **UI:** the "Instanzen" tab lists all instances with settings; the form opens full-page (Save / Save and close / Close).
- **OAuth (cloud adapters):** declare providers with `AdapterConfigSchema.WithOAuth(new OAuthProviderSpec(...))` (authorize/token URL, keys of
  clientId/secret/token/refreshToken/expiry fields). The form then shows a login button: the host opens an isolated Chrome/Edge window
  (temporary profile, private mode, deleted afterwards), receives the code on `http://localhost:<RedirectPort>/callback` (default 53682 –
  register this redirect URI with the provider; PKCE and `state` are verified) and writes the tokens into the instance config
  (currently plaintext in `data/adapter-config`; to be replaced by secure storage). Adapters can renew tokens with
  `Context.AdapterRegistry.OAuth.RefreshAsync(...)` (see the Alexa adapter). The browser opens on the host machine; without Chrome/Edge the UI shows the login link instead (not isolated).

---

# Legacy Adapters (Node.js)

Install in the Admin UI under **Adapter → "+ Legacy-Adapter (ZIP)"**. The ZIP must contain a *built* adapter (`io-package.json`, entry file such as `main.js`, and `node_modules`; the package may sit in one subfolder). It is unpacked to `Adapters\IOBroker\<name>`, and every `@iobroker/adapter-core` is replaced by the bundled bridge-compatible drop-in. Afterwards use **"+ Instanz hinzufügen"** to create `name.0`, `name.1`, … (persisted in `adapter.legacyInstances`); start/stop/restart and configuration are available in the "Instanzen" tab. All persisted instances start with the system. `adapter.legacyAutoStart = true` additionally adopts already present packages as `name.0`. Requires Node.js on the host.

---

# Snapshot Format

### Description
JSONL file containing history samples.

### Example
```json { "Id": "temp.livingroom", "Timestamp": "2026-09-29T12:57:00Z", "Value": 22.5, "Embedding": [0.12, 0.88, ...] } ```

## License

MIT, see [LICENSE](./LICENSE).
