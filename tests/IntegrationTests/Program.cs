using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ioBroker_NewGen.Controllers;
using ioBroker_NewGen.Adapters.Alexa;
using ioBroker_NewGen.Adapters.Bosch;
using ioBroker_NewGen.Adapters.FritzBox;
using ioBroker_NewGen.Adapters.Hue;
using ioBroker_NewGen.Adapters.Homematic;
using ioBroker_NewGen.Adapters.Knx;
using ioBroker_NewGen.Adapters.Shelly;
using ioBroker_NewGen.Adapters.Zigbee;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Adapters;
using ioBroker_NewGen.Core.Adapters.Protocol;
using ioBroker_NewGen.Core.Backup;
using ioBroker_NewGen.Core.Bridge;
using ioBroker_NewGen.Core.Deployment;
using ioBroker_NewGen.Core.Events;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Migration;
using ioBroker_NewGen.Core.Registry;
using ioBroker_NewGen.Core.Routing;
using ioBroker_NewGen.Runtime;
using RSEV.Utilities.ApiBuilder;
using RSEV.Utilities.Configuration;
using RSEV.Utilities.Logging;
using RSEV.Utilities.Processes;
using RSEV.Utilities.Events;
using RSEV.Utilities.Runtime;

var snapshotPath = Path.Combine(Path.GetTempPath(), $"newgen-smoke-{Guid.NewGuid():N}.jsonl");
AssertEx.Equal(
    new Version(3, 0, 0, 0),
    typeof(BaseEventBus).Assembly.GetName().Version,
    "Die Integration muss gegen RSEV.Utilities 3.0.0.0 laufen.");
AssertEx.Equal(
    "beta0.2.0",
    typeof(MainRuntimeContext).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion,
    "Die Build-Kennung muss beta0.2.0 sein.");
var eventBus = new SystemEventBus();
var matrix = new SemanticStateMatrix(snapshotPath, eventBus: eventBus);
var systemBus = new SystemMessageBus();
var stateEvents = 0;
eventBus.StateChanged.Subscribe(new StateChangedHandler(() => stateEvents++));

matrix.RegisterObject(new MatrixObject
{
    Id = "temp.livingroom",
    Name = "Wohnzimmer Temperatur",
    Type = "state",
    StateDefinition = new StateDefinition
    {
        ValueType = "double",
        Min = -40,
        Max = 80,
        Unit = "°C",
        Role = "value.temperature"
    }
});

systemBus.Subscribe("*", frame => matrix.ApplyFrame(frame));
var payload = Encoding.UTF8.GetBytes("22.5");
systemBus.Publish(BusFrame.Create(
    "mqtt.0",
    "temp.livingroom",
    payload,
    "double",
    BusFrameKind.StateUpdate));

AssertEx.Equal(22.5d, matrix.GetState("temp.livingroom")?.Value, "StateUpdate muss die Matrix erreichen.");
AssertEx.Equal(1, stateEvents, "StateChanged muss über RSEV.Utilities ausgelöst werden.");
AssertEx.True(matrix.SetState("temp.livingroom", 23.0d), "Gültige Werte müssen geschrieben werden.");
AssertEx.True(!matrix.SetState("temp.livingroom", 100.0d), "Werte außerhalb der Grenzen müssen abgewiesen werden.");
AssertEx.True(matrix.DisplayAsJson().IndexOf("temp.livingroom", StringComparison.Ordinal) >= 0, "Objektbaum muss den State enthalten.");
AssertEx.True(
    !matrix.RegisterObject(new MatrixObject
    {
        Id = "invalid.definition",
        Name = "Ungültige Definition",
        Type = "state",
        StateDefinition = new StateDefinition { Min = 10, Max = 1, Role = "value" }
    }),
    "Ungültige State-Definitionen müssen abgewiesen werden.");

var migrationCatalog = new AdapterMigrationCatalog();
AssertEx.Equal(10, migrationCatalog.All.Count, "Der Migrationskatalog muss alle Sprint-7/9/10-Adapter enthalten.");
AssertEx.True(
    migrationCatalog.All.All(descriptor => descriptor.VerificationStatus == "native-verified"),
    "Alle Sprint-7/9/10-Adapter müssen über lokale Protokoll-Fixtures verifiziert sein.");

var registry = new AdapterRegistry(eventBus);
var first = registry.Register("mqtt", AdapterType.IoBroker);
var second = registry.Register("mqtt", AdapterType.IoBroker);
AssertEx.Equal("mqtt.0", first.Id, "Erste Adapter-ID muss mit .0 beginnen.");
AssertEx.Equal("mqtt.1", second.Id, "Zweite Adapter-ID muss mit .1 fortlaufen.");

AssertEx.True(matrix.RemoveObject("temp.livingroom"), "CRUD-Delete muss das Objekt entfernen.");
AssertEx.True(matrix.GetObject("temp.livingroom") is null, "Gelöschtes Objekt darf nicht mehr vorhanden sein.");

var tempRoot = Path.Combine(Path.GetTempPath(), $"newgen-integration-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
File.WriteAllText(Path.Combine(tempRoot, "config.toml"), "[node]\nnodeId = \"test\"\n");
File.WriteAllText(Path.Combine(tempRoot, "snapshot.jsonl"), "{}\n");
File.WriteAllText(Path.Combine(tempRoot, "roles.json"), "{}\n");
var backupService = new BackupService(tempRoot);
var backupPath = await backupService.CreateBackupAsync(Path.Combine(tempRoot, "backups"));
AssertEx.True(await backupService.VerifyBackupAsync(backupPath), "Backup-Manifest und Checksummen müssen prüfbar sein.");
AssertEx.True(File.Exists(Path.Combine(backupPath, "roles.json")), "Rollenablage muss im Backup enthalten sein.");

var installer = new AdapterInstaller(Path.Combine(tempRoot, "Adapters"));
installer.EnsureLayout();
var installed = installer.InstallCustomAssembly(Path.Combine(AppContext.BaseDirectory, "RSEV.Utilities.dll"));
AssertEx.True(File.Exists(installed), "Custom-Assembly muss installierbar sein.");

var packageRoot = Path.Combine(tempRoot, "package");
Directory.CreateDirectory(packageRoot);
File.Copy(Path.Combine(AppContext.BaseDirectory, "RSEV.Utilities.dll"), Path.Combine(packageRoot, "PackageFixture.dll"));
var packageManifest = await AdapterPackageInstaller.CreateManifestAsync(
    packageRoot,
    "package-fixture",
    "1.0.0",
    "PackageFixture.dll");
var packageInstaller = new AdapterPackageInstaller(installer);
var validatedPackage = await packageInstaller.ValidateAsync(packageRoot);
AssertEx.Equal(packageManifest.Name, validatedPackage.Name, "Adapterpaket-Manifest muss validiert werden.");
await packageInstaller.InstallAsync(packageRoot);
AssertEx.True(File.Exists(Path.Combine(installer.CustomPath, "PackageFixture.dll")), "Geprüftes Adapterpaket muss installierbar sein.");

var archivePath = Path.Combine(tempRoot, "package-fixture.zip");
ZipFile.CreateFromDirectory(packageRoot, archivePath);
var archiveBytes = await File.ReadAllBytesAsync(archivePath);
var repositoryIndex = JsonSerializer.Serialize(new
{
    packages = new[]
    {
        new
        {
            name = "package-fixture",
            version = "1.0.0",
            url = "package-fixture.zip",
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant()
        }
    }
});
using (var repositoryHttp = new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>
{
    ["https://repository.test/index.json"] = Encoding.UTF8.GetBytes(repositoryIndex),
    ["https://repository.test/package-fixture.zip"] = archiveBytes
})))
{
    var repositoryClient = new AdapterRepositoryClient(packageInstaller, repositoryHttp);
    var remoteManifest = await repositoryClient.DownloadAndInstallAsync(
        new Uri("https://repository.test/index.json"),
        "package-fixture",
        "1.0.0",
        Path.Combine(tempRoot, "staging"));
    AssertEx.Equal("1.0.0", remoteManifest.Version, "Remote-Adapterpaket muss über den Repository-Client installierbar sein.");
}

using (var signingKey = System.Security.Cryptography.RSA.Create(2048))
{
    var signature = Convert.ToBase64String(signingKey.SignData(
        archiveBytes,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1));
    var signedRepositoryIndex = JsonSerializer.Serialize(new
    {
        packages = new[]
        {
            new
            {
                name = "package-fixture",
                version = "1.0.0",
                url = "package-fixture.zip",
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant(),
                signatureBase64 = signature
            }
        }
    });

    using var signedRepositoryHttp = new HttpClient(new StaticHttpHandler(new Dictionary<string, byte[]>
    {
        ["https://signed-repository.test/index.json"] = Encoding.UTF8.GetBytes(signedRepositoryIndex),
        ["https://signed-repository.test/package-fixture.zip"] = archiveBytes
    }));
    var signedRepositoryClient = new AdapterRepositoryClient(
        packageInstaller,
        signedRepositoryHttp,
        new AdapterRepositorySecurityOptions
        {
            RequireSignature = true,
            TrustedPublicKeyPem = signingKey.ExportSubjectPublicKeyInfoPem()
        });
    var signedManifest = await signedRepositoryClient.DownloadAndInstallAsync(
        new Uri("https://signed-repository.test/index.json"),
        "package-fixture",
        "1.0.0",
        Path.Combine(tempRoot, "signed-staging"));
    AssertEx.Equal("package-fixture", signedManifest.Name, "Signierte Remote-Adapterpakete müssen mit einem konfigurierten Trust-Key installierbar sein.");
}

var publishRoot = Path.Combine(tempRoot, "publish");
var releaseRoot = Path.Combine(tempRoot, "release-candidate");
Directory.CreateDirectory(publishRoot);
File.Copy(Path.Combine(AppContext.BaseDirectory, "ioBroker-NewGen.dll"), Path.Combine(publishRoot, "ioBroker-NewGen.dll"));
File.Copy(Path.Combine(AppContext.BaseDirectory, "RSEV.Utilities.dll"), Path.Combine(publishRoot, "RSEV.Utilities.dll"));
File.WriteAllText(Path.Combine(publishRoot, "config.toml"), "[node]\nnodeId = \"release\"\n");
File.WriteAllText(Path.Combine(publishRoot, "snapshot.jsonl"), string.Empty);
var releaseBuilder = new ReleaseCandidateBuilder();
#if DEBUG
const string buildConfiguration = "Debug";
#else
const string buildConfiguration = "Release";
#endif
var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var releaseAdapterDirectory = Path.Combine(repositoryRoot, "bin", buildConfiguration, "net9.0", "Adapters", "custom");
AssertEx.True(Directory.Exists(releaseAdapterDirectory), "Der Integrationstest muss die Adapter-DLLs der aktuellen Build-Konfiguration finden.");
await releaseBuilder.BuildAsync(publishRoot, releaseRoot, adapterAssemblyDirectories: new[] { releaseAdapterDirectory });
AssertEx.True(await releaseBuilder.VerifyAsync(releaseRoot), "Release-Candidate-Manifest und Hashes müssen verifizierbar sein.");
var requiredAdapterAssemblies = new[]
{
    "AdminUI.dll", "MqttAdapter.dll", "ZigbeeAdapter.dll", "HueAdapter.dll", "ShellyAdapter.dll",
    "FritzBoxAdapter.dll", "BoschAdapter.dll", "KnxAdapter.dll", "ModbusAdapter.dll",
    "HomematicAdapter.dll", "AlexaAdapter.dll"
};
AssertEx.True(
    requiredAdapterAssemblies.All(assembly => File.Exists(Path.Combine(releaseRoot, "Adapters", "custom", assembly))),
    "Release-Candidate muss alle externen Adapter-DLLs enthalten.");

using (var releaseSigningKey = System.Security.Cryptography.RSA.Create(2048))
{
    var signedReleaseRoot = Path.Combine(tempRoot, "signed-release-candidate");
    var publicKeyPem = releaseSigningKey.ExportSubjectPublicKeyInfoPem();
    await releaseBuilder.BuildAsync(
        publishRoot,
        signedReleaseRoot,
        adapterAssemblyDirectories: new[] { releaseAdapterDirectory },
        signingOptions: new ReleaseCandidateSigningOptions
        {
            PrivateKeyPem = releaseSigningKey.ExportPkcs8PrivateKeyPem(),
            PublicKeyPem = publicKeyPem,
            RequireSignature = true
        });
    AssertEx.True(
        await releaseBuilder.VerifyAsync(
            signedReleaseRoot,
            trustedPublicKeyPem: publicKeyPem,
            requireSignature: true),
        "Ein signierter Release-Candidate muss mit dem Trust-Key verifizierbar sein.");

    using var wrongKey = System.Security.Cryptography.RSA.Create(2048);
    AssertEx.True(
        !await releaseBuilder.VerifyAsync(
            signedReleaseRoot,
            trustedPublicKeyPem: wrongKey.ExportSubjectPublicKeyInfoPem(),
            requireSignature: true),
        "Ein Release-Candidate darf mit einem falschen Trust-Key nicht verifiziert werden.");
    AssertEx.True(
        !await releaseBuilder.VerifyAsync(
            signedReleaseRoot,
            trustedPublicKeyPem: "kein gültiger PEM-Key",
            requireSignature: true),
        "Ein beschädigter Trust-Key muss als ungültige Signatur behandelt werden.");
}

if (args.Length == 2 && string.Equals(args[0], "--release-publish", StringComparison.OrdinalIgnoreCase))
{
    var actualPublishDirectory = Path.GetFullPath(args[1]);
    var actualReleaseRoot = Path.Combine(tempRoot, "actual-release-candidate");
    await releaseBuilder.BuildAsync(actualPublishDirectory, actualReleaseRoot);
    AssertEx.True(
        await releaseBuilder.VerifyAsync(actualReleaseRoot),
        "Das reale dotnet-publish-Verzeichnis muss direkt als Release-Candidate verifizierbar sein.");
    AssertEx.True(
        requiredAdapterAssemblies.All(assembly => File.Exists(Path.Combine(actualReleaseRoot, "Adapters", "custom", assembly))),
        "Der reale Release-Candidate muss alle externen Adapter-DLLs enthalten.");
}

var adapterMatrix = new SemanticStateMatrix(Path.Combine(tempRoot, "adapter-state.jsonl"));
var adapterBus = new SystemMessageBus();
adapterBus.Subscribe("*", frame => adapterMatrix.ApplyFrame(frame));
var logger = new SystemLog(Path.Combine(tempRoot, "logs"));
logger.Init();
var internallyRouted = false;
adapterBus.Subscribe("router.test", _ => internallyRouted = true);
new AdapterRouter(adapterBus, logger).Route(
    new BusFrame("custom.0", "router.test", Encoding.UTF8.GetBytes("ok"), "string", BusFrameKind.Command, DateTime.UtcNow, BusFrameMetadata.Empty),
    AdapterType.Custom);
AssertEx.True(internallyRouted, "AdapterRouter muss CustomAdapter-Frames intern an den SystemBus routen.");
var adapterRegistry = new AdapterRegistry();
var adapterRouter = new AdapterRouter(adapterBus, logger);
var fakeMqtt = new FakeMqttClient();
var mqttAdapter = new TestMqttAdapter(fakeMqtt)
{
    Id = "mqtt.0"
};
await mqttAdapter.InitializeAsync(new TestAdapterContext(adapterMatrix, adapterBus, adapterRouter, adapterRegistry));
await mqttAdapter.StartAsync();
await fakeMqtt.EmitAsync("home/livingroom/temperature", Encoding.UTF8.GetBytes("22.5"));
AssertEx.True(adapterMatrix.GetState("mqtt.home.livingroom.temperature.value") is not null, "MQTT-State muss in der Matrix ankommen.");
mqttAdapter.ReceivePayload(new BusFrame(
    "mqtt.0",
    "home/livingroom/set",
    Encoding.UTF8.GetBytes("on"),
    "raw",
    BusFrameKind.Command,
    DateTime.UtcNow,
    new BusFrameMetadata { Values = { ["topic"] = "home/livingroom/set" } }));
await Task.Delay(25);
AssertEx.Equal("home/livingroom/set", fakeMqtt.LastPublishedTopic, "MQTT-Commands müssen an das Topic gesendet werden.");
await mqttAdapter.SendPayload(new BusFrame(
    "mqtt.0",
    "home/livingroom/set",
    Encoding.UTF8.GetBytes("off"),
    "raw",
    BusFrameKind.Command,
    DateTime.UtcNow,
    new BusFrameMetadata { Values = { ["topic"] = "home/livingroom/set" } }));
AssertEx.Equal("home/livingroom/set", fakeMqtt.LastPublishedTopic, "SendPayload-Alias muss den Adaptervertrag bedienen.");
await mqttAdapter.StopAsync();
AssertEx.Equal(0, mqttAdapter.Dependencies.Count, "Adapter ohne Override muss keine Dependencies besitzen.");
await PhaseOneAdapterSmoke.RunAsync(adapterMatrix, adapterBus, adapterRouter, adapterRegistry);
await PhaseTwoThreeAdapterSmoke.RunAsync(adapterMatrix, adapterBus, adapterRouter, adapterRegistry);
await MqttWireSmoke.RunAsync();
await MqttWireSmoke.BridgeWireSmoke.RunAsync(tempRoot);
await MqttWireSmoke.ModbusWireSmoke.RunAsync();
await MqttWireSmoke.NodeRuntimeSmoke.RunAsync(tempRoot);

var permissionMatrix = new SemanticStateMatrix(Path.Combine(tempRoot, "permissions.jsonl"));
permissionMatrix.RegisterObject(new MatrixObject { Id = "secret", Name = "Secret", Type = "state" });
AssertEx.True(permissionMatrix.SetPermissions("viewer", "secret", canRead: false, canWrite: false), "Permissions müssen konfigurierbar sein.");
AssertEx.True(permissionMatrix.GetState("secret", "viewer") is null, "Leserechte müssen wirksam sein.");

var roleStorePath = Path.Combine(tempRoot, "roles.json");
var roleMatrix = new SemanticStateMatrix(
    Path.Combine(tempRoot, "role-state.jsonl"),
    roleService: new DefaultRoleService(roleStorePath));
roleMatrix.RegisterObject(new MatrixObject { Id = "role-secret", Name = "Role Secret", Type = "state" });
var roleManagement = (IRoleManagementService)roleMatrix.RoleService;
AssertEx.True(roleManagement.UpsertRole("viewer", "Read-only user"), "Rollen müssen anlegbar sein.");
AssertEx.True(roleManagement.SetRolePermissions("viewer", "role-secret", canRead: false, canWrite: false), "Rollenrechte müssen konfigurierbar sein.");
AssertEx.True(roleManagement.AssignRole("alice", "viewer"), "Rollen müssen Benutzern zuweisbar sein.");
AssertEx.True(roleMatrix.GetState("role-secret", "alice") is null, "Rollen-Leserechte müssen wirksam sein.");
var reloadedRoleService = new DefaultRoleService(roleStorePath);
AssertEx.True(!reloadedRoleService.CanRead("alice", "role-secret"), "Rollen müssen persistent geladen werden.");
AssertEx.True(roleManagement.RevokeRole("alice", "viewer"), "Rollen müssen entziehbar sein.");

var dependencyOrder = new List<string>();
var mainRuntime = MainRuntimeContext.Instance;
mainRuntime.InitializeAdapterDeployment(Path.Combine(tempRoot, "runtime-adapters"));
mainRuntime.InitializeMatrixAndBus(Path.Combine(tempRoot, "dependency-state.jsonl"));
mainRuntime.InitializeCustomAdapterRuntime();
var customRuntime = mainRuntime.CustomAdapterRuntime!;
var dependencyAdapter = new ProbeAdapter("dependency-base", Array.Empty<string>(), dependencyOrder);
var dependentAdapter = new ProbeAdapter("dependent", new[] { "dependency-base" }, dependencyOrder);
customRuntime.Register(dependentAdapter);
customRuntime.Register(dependencyAdapter);
AssertEx.True(await customRuntime.StartAdapterAsync(dependentAdapter.Id), "Ein Adapter mit Dependency muss startbar sein.");
AssertEx.Equal("dependency-base", dependencyOrder[0], "Dependencies müssen vor dem abhängigen Adapter starten.");
AssertEx.Equal("dependent", dependencyOrder[1], "Der abhängige Adapter muss nach seiner Dependency starten.");
dependencyOrder.Clear();
await customRuntime.StopAllAsync();
await SecuritySmoke.RunAsync(mainRuntime, tempRoot);
AssertEx.Equal("dependent", dependencyOrder[0], "Abhängige Adapter müssen vor ihren Dependencies stoppen.");
AssertEx.Equal("dependency-base", dependencyOrder[1], "Dependencies müssen zuletzt stoppen.");

var concurrentStartOrder = new List<string>();
var concurrentAdapter = new ProbeAdapter("concurrent-start", Array.Empty<string>(), concurrentStartOrder);
customRuntime.Register(concurrentAdapter);
var concurrentStartResults = await Task.WhenAll(
    Enumerable.Range(0, 8).Select(_ => customRuntime.StartAdapterAsync(concurrentAdapter.Id)));
AssertEx.True(concurrentStartResults.All(result => result), "Parallele Startaufrufe müssen erfolgreich sein.");
AssertEx.Equal(1, concurrentStartOrder.Count(name => name == "concurrent-start"), "Ein Adapter darf bei parallelen Startaufrufen nur einmal initialisiert werden.");
await customRuntime.StopAdapterAsync(concurrentAdapter.Id);

var missingDependencyAdapter = new ProbeAdapter("missing-dependency", new[] { "not-installed" }, dependencyOrder);
customRuntime.Register(missingDependencyAdapter);
AssertEx.True(!await customRuntime.StartAdapterAsync(missingDependencyAdapter.Id), "Fehlende Dependencies müssen den Start verhindern.");
AssertEx.Equal(AdapterStatus.Faulted, mainRuntime.AdapterRegistry.Get(missingDependencyAdapter.Id)?.Status, "Fehlende Dependencies müssen den Adapter auf Faulted setzen.");

var cycleFirst = new ProbeAdapter("cycle-first", new[] { "cycle-second" }, dependencyOrder);
var cycleSecond = new ProbeAdapter("cycle-second", new[] { "cycle-first" }, dependencyOrder);
customRuntime.Register(cycleFirst);
customRuntime.Register(cycleSecond);
AssertEx.True(!await customRuntime.StartAdapterAsync(cycleFirst.Id), "Zyklische Dependencies müssen den Start verhindern.");
AssertEx.Equal(AdapterStatus.Faulted, mainRuntime.AdapterRegistry.Get(cycleFirst.Id)?.Status, "Zyklus-Start muss cycle-first auf Faulted setzen.");
AssertEx.Equal(AdapterStatus.Faulted, mainRuntime.AdapterRegistry.Get(cycleSecond.Id)?.Status, "Zyklus-Start muss cycle-second auf Faulted setzen.");

var customAssemblyDirectory = releaseAdapterDirectory;
var loadedAdapters = customRuntime.LoadAdapters(customAssemblyDirectory);
AssertEx.True(loadedAdapters.Any(adapter => adapter.Name == "admin"), "AdminUI muss als externe Adapter-Assembly geladen werden.");
var adminPortProbe = new TcpListener(IPAddress.Loopback, 0);
adminPortProbe.Start();
var adminPort = ((IPEndPoint)adminPortProbe.LocalEndpoint).Port;
adminPortProbe.Stop();
mainRuntime.Config.Set("admin.adminEnabled", true);
mainRuntime.Config.Set("admin.adminPort", adminPort);
mainRuntime.Config.Set("admin.adminRequireAuthentication", true);
mainRuntime.Config.Set("admin.adminToken", "integration-secret");
AssertEx.True(await customRuntime.StartAdapterAsync("admin"), "Externe AdminUI muss über den CustomAdapterRuntime starten.");
await Task.Delay(100);
using (var httpClient = new HttpClient())
{
    var dashboard = await httpClient.GetAsync($"http://127.0.0.1:{adminPort}/admin");
    AssertEx.Equal(System.Net.HttpStatusCode.OK, dashboard.StatusCode, "Admin-Dashboard muss erreichbar sein.");
    foreach (var route in new[] { "/", "/index.html" })
    {
        var entry = await httpClient.GetAsync($"http://127.0.0.1:{adminPort}{route}");
        AssertEx.Equal(System.Net.HttpStatusCode.OK, entry.StatusCode, $"Admin-Dashboard muss unter '{route}' erreichbar sein.");
    }
    var unauthorized = await httpClient.GetAsync($"http://127.0.0.1:{adminPort}/api/states");
    AssertEx.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode, "Admin-API muss ohne Token geschützt sein.");
    using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{adminPort}/api/states");
    authorizedRequest.Headers.Add("X-Admin-Token", "integration-secret");
    var authorized = await httpClient.SendAsync(authorizedRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, authorized.StatusCode, "Admin-API muss ein gültiges Token akzeptieren.");
    using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{adminPort}/api/adapters");
    bearerRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "integration-secret");
    var bearerResponse = await httpClient.SendAsync(bearerRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, bearerResponse.StatusCode, "Admin-API muss Bearer-Authentifizierung akzeptieren.");
    using var createRoleRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{adminPort}/api/roles")
    {
        Content = new StringContent("{\"name\":\"operator\",\"description\":\"Operator\",\"stateId\":\"secret\",\"canRead\":true,\"canWrite\":false}", Encoding.UTF8, "application/json")
    };
    createRoleRequest.Headers.Add("X-Admin-Token", "integration-secret");
    var createRoleResponse = await httpClient.SendAsync(createRoleRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, createRoleResponse.StatusCode, "Admin muss Rollen anlegen können.");
    using var assignRoleRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{adminPort}/api/users")
    {
        Content = new StringContent("{\"user\":\"bob\",\"role\":\"operator\"}", Encoding.UTF8, "application/json")
    };
    assignRoleRequest.Headers.Add("X-Admin-Token", "integration-secret");
    var assignRoleResponse = await httpClient.SendAsync(assignRoleRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, assignRoleResponse.StatusCode, "Admin muss Rollen Benutzern zuweisen können.");
    using var listRolesRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{adminPort}/api/roles");
    listRolesRequest.Headers.Add("X-Admin-Token", "integration-secret");
    var listRolesResponse = await httpClient.SendAsync(listRolesRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, listRolesResponse.StatusCode, "Admin muss Rollen auflisten können.");
    using var configRequest = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{adminPort}/api/config")
    {
        Content = new StringContent("{\"node.instanceName\":\"IntegrationTest\"}", Encoding.UTF8, "application/json")
    };
    configRequest.Headers.Add("X-Admin-Token", "integration-secret");
    var configResponse = await httpClient.SendAsync(configRequest);
    AssertEx.Equal(System.Net.HttpStatusCode.OK, configResponse.StatusCode, "Admin muss erlaubte Konfiguration persistieren können.");
}
await customRuntime.DisposeAsync();
await customRuntime.DisposeAsync();

var performanceWatch = Stopwatch.StartNew();
var performanceMatrix = new SemanticStateMatrix(Path.Combine(tempRoot, "performance-state.jsonl"));
for (var index = 0; index < 2000; index++)
{
    performanceMatrix.RegisterObject(new MatrixObject { Id = $"performance.{index}", Name = $"Performance {index}", Type = "state" });
    performanceMatrix.SetState($"performance.{index}", index);
}
performanceWatch.Stop();
AssertEx.True(performanceWatch.Elapsed < TimeSpan.FromSeconds(10), "Matrix-Basislast muss innerhalb des Performance-Smoke-Limits bleiben.");

var concurrentMatrix = new SemanticStateMatrix(Path.Combine(tempRoot, "concurrent-state.jsonl"));
Parallel.For(0, 512, index =>
{
    var stateId = $"concurrent.{index}";
    concurrentMatrix.RegisterObject(new MatrixObject { Id = stateId, Name = stateId, Type = "state" });
    AssertEx.True(concurrentMatrix.SetState(stateId, index), "Parallele Matrix-Schreibzugriffe müssen erfolgreich sein.");
});
AssertEx.True(
    Enumerable.Range(0, 512).All(index => concurrentMatrix.GetState($"concurrent.{index}")?.Value is int value && value == index),
    "ReaderWriterLockSlim muss konsistente parallele State-Werte liefern.");
concurrentMatrix.Dispose();

try
{
    Directory.Delete(tempRoot, recursive: true);
}
catch
{
    // Logger-Dateien können bis zum Prozessende geöffnet bleiben.
}

Console.WriteLine("Integration smoke tests passed.");

try
{
    File.Delete(snapshotPath);
}
catch
{
    // Temporäre Testdatei ist nur ein Cleanup-Best-Effort.
}

static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Erwartet: {expected}; Ist: {actual}.");
        }
    }
}

sealed class StaticHttpHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, byte[]> _responses;

    public StaticHttpHandler(IReadOnlyDictionary<string, byte[]> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_responses.TryGetValue(request.RequestUri?.ToString() ?? string.Empty, out var payload))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
    }
}

sealed class StateChangedHandler : BaseEventHandler<StateChangedEventArgs>
{
    private readonly Action _onHandle;

    public StateChangedHandler(Action onHandle)
    {
        _onHandle = onHandle;
    }

    protected override void OnHandle(object sender, StateChangedEventArgs args)
    {
        _onHandle();
    }
}

sealed class TestAdapterContext : IAdapterContext
{
    public TestAdapterContext(SemanticStateMatrix matrix, SystemMessageBus systemBus, AdapterRouter router, AdapterRegistry adapterRegistry)
    {
        Matrix = matrix;
        SystemBus = systemBus;
        Router = router;
        AdapterRegistry = adapterRegistry;
    }

    public SemanticStateMatrix Matrix { get; }

    public IRoleService RoleService => Matrix.RoleService;

    public SystemMessageBus SystemBus { get; }

    public AdapterRouter Router { get; }

    public AdapterRegistry AdapterRegistry { get; }

    public AdapterInstaller? AdapterInstaller => null;

    public AdapterPackageInstaller? AdapterPackageInstaller => null;

    public AdapterRepositoryClient? AdapterRepositoryClient => null;

    public IConfig Config => MainRuntimeContext.Instance.Config;

    public IRuntimeContext RuntimeContext => MainRuntimeContext.Instance;

    public RuntimeApiHost ApiHost => MainRuntimeContext.Instance.ApiHost;

    public void Publish(BusFrame frame) => SystemBus.Publish(frame);

    public void Route(BusFrame frame) => Router.Route(frame, AdapterType.Custom);

    public Task<bool> StartAdapterAsync(string adapterId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> StopAdapterAsync(string adapterId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

sealed class TestMqttAdapter : MqttAdapterBase
{
    public TestMqttAdapter(IMqttClient client)
        : base(new MqttAdapterOptions
        {
            AddressPrefix = "mqtt",
            Client = new MqttClientOptions { TopicFilter = "home/#" }
        }, _ => client)
    {
    }

    public override string Name => "mqtt-test";
}

sealed class ProbeAdapter : DeviceAdapterBase
{
    private readonly string _name;
    private readonly IReadOnlyCollection<string> _dependencies;
    private readonly ICollection<string> _startOrder;

    public ProbeAdapter(string name, IReadOnlyCollection<string> dependencies, ICollection<string> startOrder)
    {
        _name = name;
        _dependencies = dependencies;
        _startOrder = startOrder;
    }

    public override string Name => _name;

    public override IReadOnlyCollection<string> Dependencies => _dependencies;

    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        _startOrder.Add(Name);
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken cancellationToken)
    {
        _startOrder.Add(Name);
        return Task.CompletedTask;
    }
}

sealed class FakeMqttClient : IMqttClient
{
    public bool IsConnected { get; private set; }

    public string? LastPublishedTopic { get; private set; }

    public event Func<string, ReadOnlyMemory<byte>, Task>? MessageReceived;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        LastPublishedTopic = topic;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public async Task EmitAsync(string topic, ReadOnlyMemory<byte> payload)
    {
        var handler = MessageReceived;
        if (handler is not null)
        {
            await handler(topic, payload);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

static class PhaseOneAdapterSmoke
{
    public static async Task RunAsync(
        SemanticStateMatrix matrix,
        SystemMessageBus systemBus,
        AdapterRouter router,
        AdapterRegistry registry)
    {
        var zigbeeClient = new FakeMqttClient();
        var zigbee = new Zigbee2MqttAdapter(
            new Zigbee2MqttAdapterOptions
            {
                BaseTopic = "zigbee2mqtt",
                Client = new MqttClientOptions { TopicFilter = "zigbee2mqtt/#" }
            },
            _ => zigbeeClient);
        await zigbee.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await zigbee.StartAsync();
        await zigbeeClient.EmitAsync("zigbee2mqtt/lamp", Encoding.UTF8.GetBytes("{\"temperature\":20}"));
        AssertEx.True(matrix.GetState("zigbee.lamp.temperature") is not null, "Zigbee2MQTT-State muss aus JSON extrahiert werden.");
        await zigbee.StopAsync();

        var hueHandler = new RouteHttpHandler(
            (request, _) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/key/lights")
                {
                    return RouteHttpHandler.Json("{\"1\":{\"state\":{\"on\":true,\"bri\":200}}}");
                }

                return RouteHttpHandler.Empty();
            });
        using var hueClient = new HttpClient(hueHandler);
        var hue = new PhilipsHueAdapter(
            new PhilipsHueAdapterOptions { BridgeUri = new Uri("http://hue.test"), ApiKey = "key" },
            hueClient);
        await hue.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await hue.StartAsync();
        AssertEx.True(matrix.GetState("hue.1.on") is not null, "Hue-State muss aus der Light-Antwort entstehen.");
        await hue.StopAsync();

        var shellyHandler = new RouteHttpHandler(
            (request, _) =>
            {
                if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/rpc/Switch.GetStatus")
                {
                    return RouteHttpHandler.Json("{\"output\":true}");
                }

                return RouteHttpHandler.Empty();
            });
        using var shellyClient = new HttpClient(shellyHandler);
        var shelly = new ShellyAdapter(
            new ShellyAdapterOptions { DeviceUri = new Uri("http://shelly.test"), SwitchId = 0 },
            shellyClient);
        await shelly.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await shelly.StartAsync();
        AssertEx.True(matrix.GetState("shelly.switch.0.output") is not null, "Shelly-State muss aus dem RPC-Status entstehen.");
        await shelly.StopAsync();
    }
}

static class PhaseTwoThreeAdapterSmoke
{
    public static async Task RunAsync(
        SemanticStateMatrix matrix,
        SystemMessageBus systemBus,
        AdapterRouter router,
        AdapterRegistry registry)
    {
        var config = MainRuntimeContext.Instance.Config;
        var httpHandler = new RouteHttpHandler(
            (request, _) => request.RequestUri?.AbsolutePath switch
            {
                "/upnp/control/deviceinfo" => RouteHttpHandler.Xml("""
                    <Envelope><Body><GetInfoResponse><NewSerialNumber>fixture-123</NewSerialNumber><NewSoftwareVersion>8.0</NewSoftwareVersion><NewHardwareVersion>v2</NewHardwareVersion><NewUpTime>42</NewUpTime></GetInfoResponse></Body></Envelope>
                    """),
                "/rest/items" => RouteHttpHandler.Json("[{\"id\":\"sensor/temperature\",\"value\":21.5}]"),
                "/addons/xmlapi/state.cgi" => RouteHttpHandler.Xml("<root><datapoint ise_id=\"42\" value=\"true\" /></root>"),
                "/devices" => RouteHttpHandler.Json("{\"devices\":[{\"id\":\"echo-1\",\"online\":true}]}"),
                _ => RouteHttpHandler.Empty()
            });
        using var httpClient = new HttpClient(httpHandler);

        config.Set("fritzbox.deviceUri", "http://fritzbox.fixture");
        config.Set("fritzbox.pollIntervalMs", 1000);
        var fritzBox = new FritzBoxAdapter(httpClient);
        await fritzBox.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await fritzBox.StartAsync();
        AssertEx.True(matrix.GetState("fritzbox.serialNumber") is not null, "FRITZ!Box-State muss aus der SOAP-Antwort entstehen.");
        await fritzBox.StopAsync();

        config.Set("bosch.baseUri", "http://bosch.fixture");
        config.Set("bosch.token", "fixture-token");
        config.Set("bosch.pollIntervalMs", 1000);
        var bosch = new BoschAdapter(httpClient);
        await bosch.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await bosch.StartAsync();
        AssertEx.True(matrix.GetState("bosch.sensor.temperature") is not null, "Bosch-State muss aus der REST-Antwort entstehen.");
        await bosch.StopAsync();

        config.Set("homematic.baseUri", "http://homematic.fixture");
        config.Set("homematic.pollIntervalMs", 1000);
        var homematic = new HomematicAdapter(httpClient);
        await homematic.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await homematic.StartAsync();
        AssertEx.True(matrix.GetState("homematic.42") is not null, "Homematic-State muss aus der XML-API-Antwort entstehen.");
        await homematic.StopAsync();

        config.Set("alexa.endpoint", "http://alexa.fixture/devices");
        config.Set("alexa.token", "fixture-token");
        config.Set("alexa.pollIntervalMs", 1000);
        var alexa = new AlexaAdapter(httpClient);
        await alexa.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await alexa.StartAsync();
        AssertEx.True(matrix.GetState("alexa.echo-1") is not null, "Alexa-State muss aus der Gateway-Antwort entstehen.");
        await alexa.StopAsync();

        using var knxFixture = new UdpClient(0);
        var knxPort = ((IPEndPoint)knxFixture.Client.LocalEndPoint!).Port;
        config.Set("knx.gateway", "127.0.0.1");
        config.Set("knx.port", knxPort);
        config.Set("knx.groupAddress", "1/2/3");
        var knx = new KnxAdapter();
        await knx.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await knx.StartAsync();
        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var request = await knxFixture.ReceiveAsync(receiveTimeout.Token);
        AssertEx.True(request.Buffer.Length >= 10, "KNX-Adapter muss ein KNXnet/IP-Tunneling-Paket senden.");
        await knxFixture.SendAsync(new byte[] { 0x06, 0x10, 0x04, 0x20, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00 }, request.RemoteEndPoint);
        for (var attempt = 0; attempt < 100 && matrix.GetState("knx.1/2/3.raw") is null; attempt++)
        {
            await Task.Delay(10);
        }

        AssertEx.True(matrix.GetState("knx.1/2/3.raw") is not null, "KNX-State muss aus dem UDP-Fixture entstehen.");
        await knx.StopAsync();

        var recoveringHttpAdapter = new FailingHttpAdapter();
        await recoveringHttpAdapter.InitializeAsync(new TestAdapterContext(matrix, systemBus, router, registry));
        await recoveringHttpAdapter.StartAsync();
        await recoveringHttpAdapter.StopAsync();
    }
}

sealed class FailingHttpAdapter : HttpPollingAdapterBase
{
    public FailingHttpAdapter()
    {
        PollInterval = TimeSpan.FromSeconds(1);
    }

    public override string Name => "failing-http-fixture";

    protected override Task PollOnceAsync(CancellationToken cancellationToken)
    {
        throw new HttpRequestException("fixture network failure");
    }
}

sealed class RouteHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public RouteHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request, cancellationToken));
    }

    public static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Xml(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.Trim().TrimStart('\uFEFF'), Encoding.UTF8, "text/xml")
        };
    }

    public static HttpResponseMessage Empty()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
    }
}

static class MqttWireSmoke
{
public static async Task RunAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var messageReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    var brokerTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var connect = await ReadMqttPacketAsync(stream);
        AssertEx.Equal(1, connect.Header >> 4, "MQTT-Client muss CONNECT senden.");
        await WriteMqttPacketAsync(stream, 0x20, new byte[] { 0, 0 });

        var subscribe = await ReadMqttPacketAsync(stream);
        AssertEx.Equal(8, subscribe.Header >> 4, "MQTT-Client muss SUBSCRIBE senden.");
        var packetId = new[] { subscribe.Payload[0], subscribe.Payload[1] };
        await WriteMqttPacketAsync(stream, 0x90, new[] { packetId[0], packetId[1], (byte)0 });

        await WriteMqttPacketAsync(
            stream,
            0x30,
            BuildPublishPayload("broker/state", Encoding.UTF8.GetBytes("ok")));

        var publish = await ReadMqttPacketAsync(stream);
        AssertEx.Equal(3, publish.Header >> 4, "MQTT-Client muss PUBLISH senden.");
        var topicLength = (publish.Payload[0] << 8) | publish.Payload[1];
        var topic = Encoding.UTF8.GetString(publish.Payload, 2, topicLength);
        AssertEx.Equal("client/command", topic, "MQTT-Topic muss unverändert transportiert werden.");

        var disconnect = await ReadMqttPacketAsync(stream);
        AssertEx.Equal(14, disconnect.Header >> 4, "MQTT-Client muss sauber disconnecten.");
    });

    await using var mqttClient = new Mqtt311Client(new MqttClientOptions
    {
        Host = "127.0.0.1",
        Port = port,
        ClientId = "wire-test"
    });
    mqttClient.MessageReceived += (topic, payload) =>
    {
        messageReceived.TrySetResult($"{topic}:{Encoding.UTF8.GetString(payload.Span)}");
        return Task.CompletedTask;
    };

    await mqttClient.ConnectAsync();
    await mqttClient.SubscribeAsync("broker/#");
    AssertEx.Equal("broker/state:ok", await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)), "MQTT-PUBLISH muss empfangen werden.");
    await mqttClient.PublishAsync("client/command", Encoding.UTF8.GetBytes("on"));
    await mqttClient.DisconnectAsync();
    await brokerTask;
    listener.Stop();
}

public static class BridgeWireSmoke
{
    public static async Task RunAsync(string tempRoot)
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        var systemBus = new SystemMessageBus();
        var registry = new AdapterRegistry();
        var logger = new SystemLog(Path.Combine(tempRoot, "bridge-logs"));
        logger.Init();
        var received = new TaskCompletionSource<BusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        systemBus.Subscribe("*", frame =>
        {
            if (frame.Address == "legacy.temperature")
            {
                received.TrySetResult(frame);
            }
        });

        await using var bridge = new TcpBridgeServer(
            port,
            "127.0.0.1",
            registry,
            systemBus,
            logger,
            heartbeatTimeout: TimeSpan.FromSeconds(5));
        await bridge.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await BridgeProtocol.WriteAsync(stream, new BridgeEnvelope
        {
            Type = BridgeMessageType.Handshake,
            AdapterId = "legacy-test"
        });

        var ack = await BridgeProtocol.ReadAsync(stream);
        AssertEx.True(ack is not null && ack.Type == BridgeMessageType.HandshakeAck, "Bridge muss den Handshake bestätigen.");
        AssertEx.Equal("legacy-test.0", ack!.AdapterId, "Bridge muss die Name.id-ID vergeben.");

        await BridgeProtocol.WriteAsync(stream, new BridgeEnvelope
        {
            Type = BridgeMessageType.Frame,
            AdapterId = ack.AdapterId,
            Address = "legacy.temperature",
            ValueType = "double",
            FrameKind = (int)BusFrameKind.StateUpdate,
            PayloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("21.5"))
        });

        var incoming = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.Equal(ack.AdapterId, incoming.AdapterId, "Bridge muss die vergebene Adapter-ID am Frame erhalten.");
        AssertEx.Equal("21.5", Encoding.UTF8.GetString(incoming.Payload.Span), "Bridge muss die Payload 1:1 spiegeln.");

        var outboundRead = BridgeProtocol.ReadAsync(stream);
        var sent = await bridge.SendToAdapterAsync(
            ack.AdapterId!,
            new BusFrame(
                ack.AdapterId!,
                "legacy.command",
                Encoding.UTF8.GetBytes("on"),
                "string",
                BusFrameKind.Command,
                DateTime.UtcNow,
                BusFrameMetadata.Empty));
        AssertEx.True(sent, "Bridge muss Frames an verbundene Legacy-Adapter senden.");
        var outbound = await outboundRead.ConfigureAwait(false);
        AssertEx.True(outbound is not null && outbound.Type == BridgeMessageType.Frame, "Legacy-Adapter muss das Host-Frame erhalten.");
        AssertEx.Equal("legacy.command", outbound!.Address, "Ausgehende Bridge-Adresse muss erhalten bleiben.");
        AssertEx.Equal("on", Encoding.UTF8.GetString(Convert.FromBase64String(outbound.PayloadBase64!)), "Ausgehende Payload muss erhalten bleiben.");
    }
}

public static class ModbusWireSmoke
{
    public static async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var requestHeader = await ReadExactAsync(stream, 7);
            var requestLength = BinaryPrimitives.ReadUInt16BigEndian(requestHeader.AsSpan(4));
            _ = await ReadExactAsync(stream, requestLength - 1);
            await WriteModbusResponseAsync(stream, requestHeader, new byte[] { 3, 4, 0, 12, 0, 34 });

            var writeHeader = await ReadExactAsync(stream, 7);
            var writeLength = BinaryPrimitives.ReadUInt16BigEndian(writeHeader.AsSpan(4));
            _ = await ReadExactAsync(stream, writeLength - 1);
            await WriteModbusResponseAsync(stream, writeHeader, new byte[] { 6, 0, 2, 0, 99 });
        });

        await using var modbus = new ModbusTcpClient("127.0.0.1", port);
        await modbus.ConnectAsync();
        var values = await modbus.ReadHoldingRegistersAsync(1, 0, 2);
        AssertEx.Equal((ushort)12, values[0], "Modbus Register 0 muss gelesen werden.");
        AssertEx.Equal((ushort)34, values[1], "Modbus Register 1 muss gelesen werden.");
        await modbus.WriteSingleRegisterAsync(1, 2, 99);
        await serverTask;
        listener.Stop();
    }

    private static async Task WriteModbusResponseAsync(NetworkStream stream, byte[] requestHeader, byte[] body)
    {
        var response = new byte[7 + body.Length];
        Buffer.BlockCopy(requestHeader, 0, response, 0, 4);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(1 + body.Length));
        response[6] = requestHeader[6];
        Buffer.BlockCopy(body, 0, response, 7, body.Length);
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }
}

public static class NodeRuntimeSmoke
{
    public static async Task RunAsync(string tempRoot)
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        var systemBus = new SystemMessageBus();
        var registry = new AdapterRegistry();
        var logger = new SystemLog(Path.Combine(tempRoot, "node-logs"));
        logger.Init();
        var received = new TaskCompletionSource<BusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<BusFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameCount = 0;
        systemBus.Subscribe("*", frame =>
        {
            if (frame.Address == "legacy.node")
            {
                if (Interlocked.Increment(ref frameCount) == 1)
                {
                    received.TrySetResult(frame);
                }
                else
                {
                    recovered.TrySetResult(frame);
                }
            }
        });

        await using var bridge = new TcpBridgeServer(port, "127.0.0.1", registry, systemBus, logger, TimeSpan.FromSeconds(5));
        await bridge.StartAsync();
        await using var nodeRuntime = new NodeAdapterRuntime(
            new RuntimeProcessController(logger),
            registry,
            logger,
            crashRestartDelay: TimeSpan.FromMilliseconds(50),
            healthCheckInterval: TimeSpan.FromMilliseconds(250));
        nodeRuntime.StartHealthMonitoring();

        var nodeClientPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Legacy", "NodeAdapterClient.js"))
            .Replace("\\", "\\\\");
        var scriptPath = Path.Combine(tempRoot, "node-fixture.js");
        var crashMarkerPath = Path.Combine(tempRoot, "node-fixture-crashed.marker")
            .Replace("\\", "\\\\");
        File.WriteAllText(
            scriptPath,
            "const { NodeAdapterClient } = require('" + nodeClientPath + "');" +
            "const fs = require('fs');" +
            "const crashMarker = '" + crashMarkerPath + "';" +
            "const client = new NodeAdapterClient({ host: '127.0.0.1', port: " + port + ", name: 'node-fixture' });" +
            "client.connect().then(async () => { await client.sendFrame({ address: 'legacy.node', payload: Buffer.from('42'), valueType: 'int', frameKind: 0 }); if (!fs.existsSync(crashMarker)) { fs.writeFileSync(crashMarker, 'crashed'); setTimeout(() => process.exit(1), 750); } else { setTimeout(() => process.exit(0), 500); } });");

        var registration = await nodeRuntime.StartAdapterAsync("node-fixture", scriptPath);
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(registration.Id, frame.AdapterId, "Node-Runtime und Bridge müssen dieselbe Adapter-ID verwenden.");
        AssertEx.Equal("42", Encoding.UTF8.GetString(frame.Payload.Span), "Node-Client muss Frames über die Bridge senden.");
        AssertEx.True(registry.Get(registration.Id)?.TcpEndpoint is not null, "Node-Handshake muss den TCP-Endpunkt registrieren.");
        var recoveredFrame = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
        nodeRuntime.StopHealthMonitoring();
        AssertEx.Equal(registration.Id, recoveredFrame.AdapterId, "Crash-Recovery muss die bestehende Name.id-Identität wiederverwenden.");
        AssertEx.Equal(AdapterStatus.Connected, registry.Get(registration.Id)?.Status, "Crash-Recovery muss den Adapter wieder als Connected registrieren.");
    }
}

static async Task<(byte Header, byte[] Payload)> ReadMqttPacketAsync(NetworkStream stream)
{
    var header = (await ReadExactAsync(stream, 1))[0];
    var multiplier = 1;
    var length = 0;
    while (true)
    {
        var encoded = (await ReadExactAsync(stream, 1))[0];
        length += (encoded & 127) * multiplier;
        if ((encoded & 128) == 0)
        {
            break;
        }

        multiplier *= 128;
    }

    return (header, await ReadExactAsync(stream, length));
}

static async Task WriteMqttPacketAsync(NetworkStream stream, byte header, byte[] payload)
{
    var length = payload.Length;
    var remaining = new List<byte>();
    do
    {
        var encoded = length % 128;
        length /= 128;
        if (length > 0)
        {
            encoded |= 128;
        }

        remaining.Add((byte)encoded);
    }
    while (length > 0);

    await stream.WriteAsync(new[] { header });
    await stream.WriteAsync(remaining.ToArray());
    await stream.WriteAsync(payload);
    await stream.FlushAsync();
}

static byte[] BuildPublishPayload(string topic, byte[] payload)
{
    var topicBytes = Encoding.UTF8.GetBytes(topic);
    var result = new byte[2 + topicBytes.Length + payload.Length];
    result[0] = (byte)(topicBytes.Length >> 8);
    result[1] = (byte)topicBytes.Length;
    Buffer.BlockCopy(topicBytes, 0, result, 2, topicBytes.Length);
    Buffer.BlockCopy(payload, 0, result, 2 + topicBytes.Length, payload.Length);
    return result;
}

static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
{
    var buffer = new byte[length];
    var offset = 0;
    while (offset < length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
        if (read == 0)
        {
            throw new EndOfStreamException();
        }

        offset += read;
    }

    return buffer;
}
}
