using System;
using System.Collections.Generic;
using System.Linq;

namespace ioBroker_NewGen.Core.Migration
{
    /// <summary>
    /// Verbindlicher Katalog für Sprint 7, 9 und 10. Er trennt native
    /// Implementierungen von Legacy-Fallbacks und verhindert, dass ein
    /// Adapter ohne abgeschlossene Protokolltests als migriert gilt.
    /// </summary>
    public sealed class AdapterMigrationCatalog
    {
        private readonly IReadOnlyList<AdapterMigrationDescriptor> _descriptors;

        public AdapterMigrationCatalog()
        {
            _descriptors = new List<AdapterMigrationDescriptor>
            {
                new() { Name = "MQTT", Phase = MigrationPhase.Phase1Critical, Transport = "MQTT 3.1.1/TCP", RequiredConfiguration = new[] { "host", "port", "clientId", "topicFilter" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "MqttAdapter.dll" },
                new() { Name = "Zigbee", Phase = MigrationPhase.Phase1Critical, Transport = "Zigbee2MQTT/MQTT", RequiredConfiguration = new[] { "host", "port", "clientId", "baseTopic" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "ZigbeeAdapter.dll" },
                new() { Name = "Philips Hue", Phase = MigrationPhase.Phase1Critical, Transport = "Hue HTTP API v1", RequiredConfiguration = new[] { "bridgeUri", "apiKey" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "HueAdapter.dll" },
                new() { Name = "Shelly", Phase = MigrationPhase.Phase1Critical, Transport = "Shelly Gen2 RPC/HTTP", RequiredConfiguration = new[] { "deviceUri", "switchId" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "ShellyAdapter.dll" },
                new() { Name = "FRITZ!Box", Phase = MigrationPhase.Phase2Medium, Transport = "TR-064/HTTP", RequiredConfiguration = new[] { "deviceUri", "username", "password" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "FritzBoxAdapter.dll" },
                new() { Name = "Bosch", Phase = MigrationPhase.Phase2Medium, Transport = "Bosch Smart Home REST", RequiredConfiguration = new[] { "baseUri", "token" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "BoschAdapter.dll" },
                new() { Name = "KNX", Phase = MigrationPhase.Phase2Medium, Transport = "KNXnet/IP Tunneling", RequiredConfiguration = new[] { "gateway", "groupAddress" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "KnxAdapter.dll" },
                new() { Name = "Modbus", Phase = MigrationPhase.Phase2Medium, Transport = "Modbus TCP", RequiredConfiguration = new[] { "host", "port", "unitId", "startAddress" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "ModbusAdapter.dll" },
                new() { Name = "Homematic", Phase = MigrationPhase.Phase3Small, Transport = "CCU XML-API", RequiredConfiguration = new[] { "baseUri", "statePath" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "HomematicAdapter.dll" },
                new() { Name = "Alexa", Phase = MigrationPhase.Phase3Small, Transport = "Configurable Alexa REST Gateway", RequiredConfiguration = new[] { "endpoint", "token" }, NativeImplementationAvailable = true, ProtocolFixtureAvailable = true, ImplementationAssembly = "AlexaAdapter.dll" }
            };
        }

        public IReadOnlyList<AdapterMigrationDescriptor> All => _descriptors;

        public IReadOnlyList<AdapterMigrationDescriptor> GetByPhase(MigrationPhase phase)
        {
            return _descriptors.Where(descriptor => descriptor.Phase == phase).ToArray();
        }

        public AdapterMigrationDescriptor Get(string name)
        {
            return _descriptors.FirstOrDefault(
                       descriptor => string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeyNotFoundException($"Kein Migrationskatalogeintrag für '{name}'.");
        }
    }
}
