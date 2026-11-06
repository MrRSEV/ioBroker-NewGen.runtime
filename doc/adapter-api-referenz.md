# Adapter-API-Referenz

Referenz der API für **native C#-Adapter** (`AdapterType.Custom`). Eine
Einführung mit Beispiel steht im [Adapter SDK](adapter-sdk.md). Legacy-Adapter
(Node.js) laufen isoliert über die [TCP-Bridge](core-bridge.md) und sind hier
nicht beschrieben.

Namespaces: `ioBroker_NewGen.Core.Adapters`, `.Core.Bus`, `.Core.Matrix`,
`.Core.Routing`, `.Core.Registry`, `.Core.Web`.

## Inhalt

1. [Adapter bauen und ausliefern](#1-adapter-bauen-und-ausliefern)
2. [IAdapter](#2-iadapter)
3. [DeviceAdapterBase](#3-deviceadapterbase)
4. [IAdapterContext](#4-iadaptercontext)
5. [Lifecycle](#5-lifecycle)
6. [Bus: BusFrame](#6-bus-busframe)
7. [Matrix: Objekte und States](#7-matrix-objekte-und-states)
8. [Konfiguration](#8-konfiguration)
9. [Web-Endpunkte](#9-web-endpunkte)
10. [Lokalisierung](#10-lokalisierung)
11. [Protokoll-Basisklassen](#11-protokoll-basisklassen)
12. [Adapterpakete](#12-adapterpakete)

---

## 1. Adapter bauen und ausliefern

Ein Adapter ist eine eigene .NET-9-Assembly. Die Runtime lädt alle DLLs aus dem
Verzeichnis `Adapters\native` neben der Runtime-Exe und erzeugt jede nicht
abstrakte `IAdapter`-Implementierung über den **parameterlosen Konstruktor**.

Der Adapter referenziert die Runtime-DLL und `RSEV.Utilities.dll` nur zum
Kompilieren (`Private=false`, nicht mitkopieren). Beide DLLs liegen im `lib\`
des Adapter-Projekts und werden automatisch eingebunden:

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <Version>1.0.0</Version>
  <AssemblyName>ioBroker-NewGen.example-$(Version)</AssemblyName>
  <AssemblyVersion>1.0.0.0</AssemblyVersion>
  <FileVersion>1.0.0.0</FileVersion>
  <InformationalVersion>1.0.0</InformationalVersion>
  <OutputPath>bin\</OutputPath>
  <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup>
<ItemGroup>
  <None Remove="lib\**" />
  <LibDll Include="lib\*.dll" />
  <Reference Include="@(LibDll)"><Private>false</Private></Reference>
</ItemGroup>
```

`Version` muss vor `AssemblyName` stehen, sonst bleibt `$(Version)` leer.

> **Wichtig:** Der Assembly-Name der Runtime enthält ihre Version
> (`ioBroker-NewGen.runtime-<Version>`). Adapter binden sich an genau diesen
> Namen und müssen bei jedem Versionssprung der Runtime neu gebaut werden, mit
> der aktuellen Runtime-DLL in `lib\`.

## 2. IAdapter

`Core\Adapters\IAdapter.cs` – der einheitliche Vertrag.

| Member | Typ | Beschreibung |
|---|---|---|
| `Name` | `string` | Eindeutiger Adaptername, z. B. `"example"`. |
| `Id` | `string` (get/set) | Wird von der Registry gesetzt, Format `Name.Instanz` (z. B. `example.0`). |
| `Type` | `AdapterType` | Für native Adapter immer `AdapterType.Custom`, sonst wirft `Register` eine `InvalidOperationException`. |
| `Dependencies` | `IReadOnlyCollection<string>` | Optional (Standard: leer). Namen oder IDs von Adaptern, die zuerst gestartet werden müssen. |
| `Version` | `string?` | Optional (Standard: `null`). Versionsnummer, z. B. `"1.0.0"`. Wird bei der Registrierung in `AdapterRegistration.Version` (Metadatum `version`) übernommen. Das Admin-UI liest sie über `context.AdapterRegistry` bzw. `GET /api/adapters` (Feld `Version`). Legacy-Adapter liefern sie aus dem Paket (`io-package.json`) oder im TCP-Handshake als `Metadata["version"]`. |
| `Translations` | `IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>>` | Optional. Sprachcode auf Schlüssel/Text. |
| `LocalizationDirectory` | `string?` | Optional. Standard: Ordner `Localization` neben der Assembly. |
| `ConfigSchema` | `AdapterConfigSchema?` | Optional. Konfigurationsschema für die Admin-Oberfläche. |
| `InitializeAsync(IAdapterContext, CancellationToken)` | `Task` | Erhält den begrenzten Host-Kontext. |
| `StartAsync(CancellationToken)` | `Task` | Verbindungen und State-Synchronisation starten. |
| `StopAsync(CancellationToken)` | `Task` | Verbindungen und Timer schließen. |
| `SendPayloadAsync(BusFrame, CancellationToken)` | `Task` | Verarbeitet ein ausgehendes Frame. |
| `SendPayload(BusFrame, CancellationToken)` | `Task` | Kompatibilitätsname, delegiert an `SendPayloadAsync`. |
| `ReceivePayload(BusFrame)` | `void` | Empfängt Commands, die an `Id` adressiert sind. |

### AdapterType

| Wert | Bedeutung |
|---|---|
| `Custom` | Nativer C#-Adapter, läuft im Host, direkt über den Systembus. |
| `IoBroker` | Legacy-Adapter als Node.js-Prozess über die TCP-Bridge. |

## 3. DeviceAdapterBase

`Core\Adapters\DeviceAdapterBase.cs` – abstrakte Basisklasse mit Lifecycle,
Konfiguration und Bus-Helfern. Für die meisten Adapter der empfohlene Einstieg.
`Name` ist die einzige abstrakte Eigenschaft.

### Überschreibbare Hooks

| Hook | Aufruf |
|---|---|
| `OnInitializeAsync(CancellationToken)` | Nach Setzen des Kontexts und der Instanzkonfiguration. |
| `OnStartAsync(CancellationToken)` | Beim Start. |
| `OnStopAsync(CancellationToken)` | Beim Stopp. |
| `OnSendPayloadAsync(BusFrame, CancellationToken)` | Bei ausgehenden Frames. |
| `OnReceivePayload(BusFrame)` | Bei eingehenden Commands. |

Alle Hooks sind standardmäßig leer. Weitere virtuelle Member: `Type`
(`Custom`), `Dependencies` (leer), `ConfigSchema` (`null`).

### Geschützte Member

| Member | Beschreibung |
|---|---|
| `Context` | `IAdapterContext`. Vor `InitializeAsync` wirft der Zugriff eine `InvalidOperationException`. |
| `Config` | `IConfig` der Instanz; ohne Instanzwerte der globale `Context.Config`. |
| `RegisterState(id, name, type = "state", definition = null)` | Registriert ein `MatrixObject` in der Matrix. |
| `PublishState(address, value, valueType = "json", metadata = null)` | Sendet ein `BusFrameKind.StateUpdate` mit der eigenen `Id` als Absender. |
| `PublishCommand(adapterId, address, value, valueType = "json", metadata = null)` | Sendet ein `BusFrameKind.Command` an einen anderen Adapter. |
| `ResolvePrefix(configured, defaultName)` | Bei Zusatzinstanzen (nicht `.0`) und unverändertem Standardpräfix wird die `Id` als Präfix genutzt. |
| `BuildSchema(...)`, `TextField`, `PasswordField`, `NumberField`, `CheckboxField` | Helfer für `ConfigSchema` (siehe [Konfiguration](#8-konfiguration)). |

### Serialisierung der Werte

`PublishState` und `PublishCommand` wandeln `value` in `Payload` (UTF-8) um:

| `valueType` | Verhalten |
|---|---|
| `"raw"` | `value` muss `byte[]` sein, sonst leerer Payload. |
| `"string"`, `"text"` | `value.ToString()`. |
| alles andere (z. B. `"json"`, `"int"`, `"double"`, `"bool"`) | `JsonSerializer.Serialize(value)`. |
| `value == null` | `"null"` bei `json`, sonst leerer Text. |

## 4. IAdapterContext

`Core\Adapters\IAdapterContext.cs` – begrenzter Host-Kontext. Er wird pro
Adapterinstanz erzeugt und in `InitializeAsync` übergeben.

| Member | Beschreibung |
|---|---|
| `Matrix` | `SemanticStateMatrix`, siehe [Matrix](#7-matrix-objekte-und-states). |
| `RoleService` | Berechtigungsvertrag (`IRoleService`). Die Verwaltung liegt bei der AdminUI. |
| `Localization` | `ILocalizationService?` für Texte. |
| `SystemBus` | `SystemMessageBus`. |
| `Router` | `AdapterRouter`. |
| `AdapterRegistry` | Registry mit Status, Konfiguration und Fehlerhistorie. |
| `WebEndpoints` | `IWebEndpointRegistry` für HTTP/WebSocket, siehe [Web-Endpunkte](#9-web-endpunkte). |
| `AdapterInstaller`, `AdapterPackageInstaller`, `AdapterRepositoryClient` | Installation und Repository. Können `null` sein. |
| `Legacy` | `LegacyAdapterManager?`, `null` ohne laufende Bridge. |
| `Config` | `IConfig` (RSEV.Utilities) der globalen Konfiguration. |
| `RuntimeContext` | `IRuntimeContext`, u. a. `Logger`. |
| `ApiHost` | `RuntimeApiHost`. **Für Adapter verboten**, siehe unten. |
| `StartAdapterAsync(id)` / `StopAdapterAsync(id)` | Startet/stoppt einen Adapter, liefert `bool`. |
| `RestartAdapterAsync(id)` | Stoppt und startet neu; zählt den Neustart in der Registry. |
| `AddInstanceAsync(id)` | Legt eine weitere Instanz an, liefert die neue ID oder `null`. |
| `RemoveInstanceAsync(id)` | Entfernt eine zusätzliche Instanz (nicht `.0`). |
| `Publish(BusFrame)` | Veröffentlicht ein Frame auf dem Systembus. |
| `Route(BusFrame)` | Routet ein Frame über den `AdapterRouter`. |

> **Verbot:** `ApiHost` und `WebHostService.cs` dürfen von Adaptern nicht
> verwendet werden. Der Zugriff wird protokolliert, die betroffene Instanz wird
> isoliert beendet und auf `Faulted` gesetzt. Core und andere Adapter laufen
> weiter. HTTP/WebSocket läuft ausschließlich über `WebEndpoints`.

## 5. Lifecycle

Ablauf beim Start eines Adapters (`StartAdapterAsync`):

1. Namen werden zu einer ID aufgelöst (`example` zu `example.0`).
2. Status `Connected` bedeutet: bereits gestartet, nichts zu tun.
3. Jede Dependency wird rekursiv zuerst gestartet. Fehlt eine Dependency oder
   gibt es eine Schleife, wird der Adapter auf `Faulted` gesetzt.
4. Status `Connecting`.
5. Übersetzungen werden registriert (`Translations`, `LocalizationDirectory`).
6. `InitializeAsync` mit dem adapterbezogenen Kontext, danach `StartAsync`.
7. Status `Connected`. Bei einer Ausnahme: `Faulted` und der Fehler wird in der
   Registry gespeichert.

Beim Start des Hosts werden die Adapter aus `adapter.enabledAdapters` (Namen
oder IDs) gestartet. Fehlt die Einstellung, werden alle geladenen Adapter
gestartet. Zusätzliche Instanzen werden aus `adapter.instances` wiederhergestellt.

Start, Stopp und Neustart laufen serialisiert über ein gemeinsames Gate.
`StartAsync` sollte daher zügig zurückkehren (Hintergrund-Tasks oder Timer
verwenden), sonst warten alle anderen Lifecycle-Aufrufe.

### AdapterStatus

| Status | Bedeutung |
|---|---|
| `Created` | Registriert, noch nicht gestartet. |
| `Connecting` | Start läuft. |
| `Connected` | Aktiv und betriebsbereit. |
| `Disconnected` | Geordnet gestoppt. |
| `Faulted` | Abgestürzt oder in einem Fehlerzustand. |

Eine Ausnahme in `ReceivePayload` setzt den Adapter ebenfalls auf `Faulted`.

## 6. Bus: BusFrame

`Core\Bus\BusFrame.cs` – neutrales Transportpaket. Der Systembus kennt die
Semantik des Payloads nicht.

| Eigenschaft | Beschreibung |
|---|---|
| `AdapterId` | Absender- bzw. Zieladapter (`Name.Instanz`). |
| `Address` | Logische Adresse, z. B. eine State-ID wie `example.value`. |
| `Payload` | `ReadOnlyMemory<byte>` mit den Rohdaten. |
| `ValueType` | Typbezeichnung, z. B. `double`, `bool`, `json`, `raw`. |
| `Kind` | `BusFrameKind`. |
| `Timestamp` | Erzeugungszeitpunkt (UTC). |
| `Metadata` | `BusFrameMetadata` mit `Values` (`Dictionary<string,string>`), z. B. `correlationId`, `replyTo`, `topic`. |

`BusFrame.Create(adapterId, address, payload, valueType, kind, metadata = null)`
setzt den Zeitstempel und leere Metadaten.

### BusFrameKind

| Wert | Verwendung |
|---|---|
| `Raw` | Rohe Payloads. |
| `StateUpdate` | Zustandsänderung. Wird in die StateMatrix gespiegelt. |
| `Command` | Befehl. Adressiert den Zieladapter über `AdapterId` und landet in dessen `ReceivePayload`. |
| `Event` | Ereignis. |

### SystemMessageBus

| Methode | Beschreibung |
|---|---|
| `Publish(frame)` / `PublishAsync(frame)` | Frame veröffentlichen. |
| `Subscribe(address, Action<BusFrame>)` | Auf eine Adresse abonnieren. `"*"` abonniert alle. |
| `Unsubscribe(address, callback)` | Abo beenden. |

Weitere Details: [Core/Bus](core-bus.md), [Core/Routing](core-routing.md).

## 7. Matrix: Objekte und States

`SemanticStateMatrix` (`context.Matrix`) hält Objekte, States, Historie und den
semantischen Index. Wichtige Methoden für Adapter:

| Methode | Beschreibung |
|---|---|
| `RegisterObject(MatrixObject)` | Legt ein Gerät, einen Kanal oder Datenpunkt an. |
| `UpdateObject(MatrixObject)` | Aktualisiert ein Objekt. |
| `GetObject(id)` / `RemoveObject(id)` | Lesen/Entfernen. |
| `SetState(id, value, user = "system")` | Setzt einen Wert (prüft `StateDefinition` und Rechte). |
| `GetState(id)` / `GetState(id, user)` | Liest einen `StateNode`. |
| `GetStates(pattern = "*")` | Liest mehrere States. |
| `Subscribe(id, Action<StateNode>)` / `Unsubscribe(...)` | Änderungen verfolgen. |
| `GetHistory(id, from, to)` | Historie. |
| `SemanticQuery(text, k = 10)` | Semantische Suche. |
| `SetPermissions(user, stateId, canRead, canWrite)` | Rechte pro State. |

Der Weg über `PublishState` ist für Adapter üblich: Das `StateUpdate`-Frame
wird vom Host in die Matrix gespiegelt.

### MatrixObject

| Eigenschaft | Beschreibung |
|---|---|
| `Id` (required) | Eindeutige ID. |
| `Name` (required) | Anzeigename. |
| `Type` (required) | Typ, z. B. `state`, `device`, `channel`. |
| `Meta` | Freie Schlüssel/Wert-Paare. Fließen in das semantische Embedding ein. |
| `StateDefinition` | Optionale fachliche Regeln. |

### StateDefinition

| Eigenschaft | Beschreibung |
|---|---|
| `ValueType` | Zulässig: `any`, `object`, `null`, `bool`/`boolean`, `string`/`text`, `int`/`int32`/`integer`, `long`/`int64`, `float`/`single`, `double`/`decimal`/`number`, `json`. |
| `Min`, `Max` | Grenzen für numerische Werte. `Min` darf nicht größer als `Max` sein. |
| `Role` | Rolle, z. B. `value.temperature`. Darf nicht leer sein, wenn gesetzt. |
| `Unit` | Einheit, z. B. `°C`. Darf nicht leer sein, wenn gesetzt. |

`SetState` lehnt Werte ab, die nicht zu `ValueType`, `Min` oder `Max` passen.

Weitere Details: [Core/Matrix](core-matrix.md).

## 8. Konfiguration

Instanzwerte liegen in der Registry. `DeviceAdapterBase.Config` liefert sie mit
Rückfall auf die globale Konfiguration. Schlüssel haben das Format
`<adaptername>.<schlüssel>`.

`AdapterConfigReader.Read<T>(config, key, fallback)` liest mit stabilen
Standardwerten. Fehlende Keys, leere Strings und numerische Nullwerte liefern
den Fallback, sofern der Fallback nicht selbst leer/0 ist.

### ConfigSchema

Das Schema beschreibt Standardwerte (`Native`) und das Formular der
Admin-Oberfläche (`JsonConfig`).

```csharp
public override AdapterConfigSchema? ConfigSchema => BuildSchema(
    TextField("host", "Host", "127.0.0.1"),
    NumberField("port", "Port", 8080),
    PasswordField("password", "Passwort"),
    CheckboxField("useTls", "TLS verwenden"));
```

| Feldtyp | Fabrikmethode |
|---|---|
| `text` | `AdapterConfigField.Text(key, label, default = "", help = null)` |
| `password` | `AdapterConfigField.Password(key, label, help = null)` |
| `number` | `AdapterConfigField.Number(key, label, default, help = null)` |
| `checkbox` | `AdapterConfigField.Checkbox(key, label, default = false, help = null)` |

Alternativ: `AdapterConfigSchema.FromDirectory(dir)` liest `io-package.json`
(`native`) und `admin/jsonConfig.json`. Ohne `ConfigSchema` sucht die Runtime
diese Dateien neben der Assembly. `WithOAuth(...)` ergänzt OAuth-Anbieter.

## 9. Web-Endpunkte

Adapter betreiben keinen eigenen Webserver. Der separate Web-Adapter besitzt den
einzigen HTTP-/WebSocket-Listener (Standard `127.0.0.1:8080`). Adapter
registrieren ihre Routen über `context.WebEndpoints` und sollten
`"web"` in `Dependencies` angeben, damit der Listener zuerst läuft.

```csharp
public override IReadOnlyCollection<string> Dependencies => new[] { "web" };

protected override Task OnStartAsync(CancellationToken ct)
{
    Context.WebEndpoints.Register(
        Id,
        "/api/example",
        WebEndpointKind.Http,
        async (request, cancellationToken) =>
            WebEndpointResponse.Text(200, "ok"));
    return Task.CompletedTask;
}

protected override Task OnStopAsync(CancellationToken ct)
{
    Context.WebEndpoints.Unregister(Id, "/api/example");
    return Task.CompletedTask;
}
```

### IWebEndpointRegistry

| Member | Beschreibung |
|---|---|
| `Register(ownerId, route, kind, handler)` | `true` bei Erfolg. `false`, wenn ein anderer Besitzer die Route hält. Die Route muss mit `/` beginnen. Erneutes Registrieren durch denselben Besitzer ersetzt den Handler. |
| `Unregister(ownerId, route)` | Nur der Besitzer kann entfernen. |
| `DispatchAsync(request, ct)` | Verteilt eine Anfrage an den Handler. |
| `Registrations` | Alle aktuellen Registrierungen. |

Routen sind case-insensitiv und werden ohne abschließenden `/` normalisiert.

### Typen

| Typ | Beschreibung |
|---|---|
| `WebEndpointKind` | `Http` oder `WebSocket`. |
| `WebEndpointRequest` | `Route`, `Method` (Standard `GET`), `Headers`, `Body`, `IsWebSocket`, `WebSocket`. |
| `WebEndpointResponse` | `StatusCode` (200), `Headers`, `Body`. `WebEndpointResponse.Text(status, text, contentType)` baut eine Textantwort. |
| `WebEndpointHandler` | `Task<WebEndpointResponse>(WebEndpointRequest, CancellationToken)`. |
| `IWebSocketSession` | `IsOpen`, `SendTextAsync`, `SendBinaryAsync`, `ReceiveAsync`, `CloseAsync`. |

### Statuscodes des Dispatchers

| Code | Ursache |
|---|---|
| `404` | Route nicht registriert. |
| `426` | Art passt nicht (HTTP-Anfrage an WebSocket-Route oder umgekehrt). |
| `500` | Der Handler hat eine Ausnahme geworfen. |

## 10. Lokalisierung

Adapter erweitern die Host-Sprachdateien, überschreiben sie aber nie. Es gibt
zwei Wege:

- `Translations`: Sprachcode (`de_DE`, `en`, `fr_FR` …) auf Schlüssel/Text.
- Sprachdateien (`de_DE.json`, `en_GB.json` …) im Ordner `Localization` neben der
  Assembly oder in `LocalizationDirectory`.

Empfohlen sind Schlüssel mit Präfix `adapter.<name>.`. Details:
[Lokalisierung](localization.md).

## 11. Protokoll-Basisklassen

Namespace `Core.Adapters.Protocol`, Basis ist jeweils `DeviceAdapterBase`.

### HttpPollingAdapterBase

Gemeinsamer Lifecycle für HTTP-basierte Adapter.

| Member | Beschreibung |
|---|---|
| `HttpClient` | Wird im Konstruktor übergeben oder neu erzeugt. |
| `PollInterval` | Standard 30 s. Werte kleiner/gleich 0 fallen auf 30 s zurück. |
| `PollOnceAsync(ct)` (abstrakt) | Ein Abrufzyklus. |

Der erste Poll läuft synchron im Start. Schlägt er fehl, wird nur gewarnt und
der Start gelingt trotzdem. Weitere Polls laufen nicht überlappend.

### MqttAdapterBase

MQTT-3.1.1-Basis. Konfigurationsschlüssel `host`, `port`, `clientId`,
`username`, `password`, `useTls`, `keepAliveSeconds`, `topicFilter` und
`addressPrefix` (jeweils mit Präfix `<name>.`).

| Member | Beschreibung |
|---|---|
| `HandleIncomingMessageAsync(topic, payload)` | Standard: veröffentlicht ein `raw`-`StateUpdate` mit der Adresse `<Präfix>.<topic mit '.'>.value` und dem Topic in den Metadaten. |
| `OnSendPayloadAsync` | Publiziert den Payload auf `Metadata["topic"]`, sonst auf `Address`. |

### Weitere Bausteine

Im selben Namespace liegen `ModbusTcpClient`, `Mqtt311Client`, `IMqttClient`,
`MqttClientOptions` und `AdapterConfigReader`.

## 12. Adapterpakete

Installierbare Pakete beschreibt `AdapterPackageManifest`:

| Feld | Beschreibung |
|---|---|
| `Name` | Gültiger Dateiname ohne Pfadtrenner. |
| `Version` | Pflichtfeld. |
| `EntryAssembly` | Haupt-DLL, muss auf `.dll` enden und in `Files` enthalten sein. |
| `Dependencies` | Namen anderer Adapter. |
| `Files` | Dateiname auf SHA-256 (Schutz vor beschädigten oder veränderten Paketen). |

Regeln für `Files`: nur flache Dateinamen (keine Unterordner), nur `.dll`. Die
Runtime-DLL und `RSEV.Utilities.dll` dürfen **nicht** Teil eines Pakets sein.
Pakete werden aus lokalen Dateien oder über HTTP installiert, siehe
[Developer Guide](developer-guide.md).
