using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Bus;
using ioBroker_NewGen.Core.Matrix;
using ioBroker_NewGen.Core.Routing;
using RSEV.Utilities.Configuration;

namespace ioBroker_NewGen.Core.Adapters
{
    /// <summary>
    /// Gemeinsame Lifecycle- und Buslogik für native Geräteadapter.
    /// </summary>
    public abstract class DeviceAdapterBase : IAdapter
    {
        private IAdapterContext? _context;

        protected IAdapterContext Context => _context ?? throw new InvalidOperationException("Adapter ist noch nicht initialisiert.");

        private IConfig? _instanceConfig;

        protected IConfig Config => _instanceConfig ?? Context.Config;

        public virtual ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigSchema? ConfigSchema => null;

        protected static ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigSchema BuildSchema(
            params ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField[] fields)
            => ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigSchema.FromFields(fields);

        protected static ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField TextField(string key, string label, string defaultValue = "")
            => ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField.Text(key, label, defaultValue);

        protected static ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField PasswordField(string key, string label)
            => ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField.Password(key, label);

        protected static ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField NumberField(string key, string label, long defaultValue)
            => ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField.Number(key, label, defaultValue);

        protected static ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField CheckboxField(string key, string label, bool defaultValue = false)
            => ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigField.Checkbox(key, label, defaultValue);

        protected string ResolvePrefix(string configured, string defaultName)
            => !Id.EndsWith(".0", StringComparison.Ordinal) && !string.IsNullOrEmpty(Id) &&
               string.Equals(configured, defaultName, StringComparison.OrdinalIgnoreCase)
                ? Id
                : configured;

        public abstract string Name { get; }

        public string Id { get; set; } = string.Empty;

        public virtual AdapterType Type => AdapterType.Custom;

        public virtual IReadOnlyCollection<string> Dependencies => Array.Empty<string>();

        public async Task InitializeAsync(IAdapterContext context, CancellationToken cancellationToken = default)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            var configs = context.AdapterRegistry?.Configs;
            if (configs is not null && context.Config is not null && !string.IsNullOrEmpty(Id) && configs.HasConfig(Name))
            {
                _instanceConfig = new Configuration.InstanceConfig(context.Config, Name, configs.GetValues(Id, Name));
            }

            await OnInitializeAsync(cancellationToken);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return OnStartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return OnStopAsync(cancellationToken);
        }

        public Task SendPayloadAsync(BusFrame frame, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return OnSendPayloadAsync(frame, cancellationToken);
        }

        public Task SendPayload(BusFrame frame, CancellationToken cancellationToken = default)
        {
            return SendPayloadAsync(frame, cancellationToken);
        }

        public void ReceivePayload(BusFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            OnReceivePayload(frame);
        }

        protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected virtual Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected virtual Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected virtual Task OnSendPayloadAsync(BusFrame frame, CancellationToken cancellationToken) => Task.CompletedTask;

        protected virtual void OnReceivePayload(BusFrame frame)
        {
        }

        protected void RegisterState(
            string id,
            string name,
            string type = "state",
            StateDefinition? definition = null)
        {
            Context.Matrix.RegisterObject(new MatrixObject
            {
                Id = id,
                Name = name,
                Type = type,
                StateDefinition = definition
            });
        }

        protected void PublishState(
            string address,
            object? value,
            string valueType = "json",
            BusFrameMetadata? metadata = null)
        {
            var payload = valueType.Equals("raw", StringComparison.OrdinalIgnoreCase)
                ? value as byte[] ?? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(SerializeValue(value, valueType));

            Context.Route(BusFrame.Create(Id, address, payload, valueType, BusFrameKind.StateUpdate, metadata));
        }

        protected void PublishCommand(
            string adapterId,
            string address,
            object? value,
            string valueType = "json",
            BusFrameMetadata? metadata = null)
        {
            var payload = valueType.Equals("raw", StringComparison.OrdinalIgnoreCase)
                ? value as byte[] ?? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(SerializeValue(value, valueType));

            Context.Route(new BusFrame(
                adapterId,
                address,
                payload,
                valueType,
                BusFrameKind.Command,
                DateTime.UtcNow,
                metadata ?? BusFrameMetadata.Empty));
        }

        private static string SerializeValue(object? value, string valueType)
        {
            if (value is null)
            {
                return valueType.Equals("json", StringComparison.OrdinalIgnoreCase) ? "null" : string.Empty;
            }

            if (valueType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                valueType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                return value.ToString() ?? string.Empty;
            }

            return JsonSerializer.Serialize(value);
        }
    }
}
