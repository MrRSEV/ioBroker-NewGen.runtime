using System;
using System.Threading;
using System.Threading.Tasks;
using ioBroker_NewGen.Core.Adapters;
using ioBroker_NewGen.Core.Bus;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    public sealed class MqttAdapterOptions
    {
        public MqttClientOptions Client { get; init; } = new();

        public string AddressPrefix { get; init; } = "mqtt";
    }

    /// <summary>
    /// Gemeinsame MQTT-Adapterbasis. Die konkrete Adapteridentität liegt in
    /// einer separaten Adapter-Assembly; der Transport gehört zur Core-Runtime.
    /// </summary>
    public abstract class MqttAdapterBase : DeviceAdapterBase
    {
        private readonly Func<MqttClientOptions, IMqttClient> _clientFactory;
        private IMqttClient? _client;

        protected MqttAdapterBase(
            MqttAdapterOptions options,
            Func<MqttClientOptions, IMqttClient>? clientFactory = null)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _clientFactory = clientFactory ?? (clientOptions => new Mqtt311Client(clientOptions));
        }

        protected MqttAdapterOptions Options { get; private set; }

        protected virtual string ConfigurationPrefix => Name;

        protected virtual string AddressPrefixConfigurationKey => "addressPrefix";

        public override ioBroker_NewGen.Core.Adapters.Configuration.AdapterConfigSchema? ConfigSchema
        {
            get
            {
                var client = new MqttClientOptions();
                return BuildSchema(
                    TextField("host", "Broker-Host", client.Host),
                    NumberField("port", "Port", client.Port),
                    TextField("clientId", "Client-ID", client.ClientId),
                    TextField("username", "Benutzername", string.Empty),
                    PasswordField("password", "Passwort"),
                    CheckboxField("useTls", "TLS verwenden", client.UseTls),
                    NumberField("keepAliveSeconds", "Keep-Alive (s)", client.KeepAliveSeconds),
                    TextField("topicFilter", "Topic-Filter", client.TopicFilter),
                    TextField(AddressPrefixConfigurationKey, "Adresspräfix", Options.AddressPrefix));
            }
        }

        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            var prefix = ConfigurationPrefix;
            var client = Options.Client;
            Options = new MqttAdapterOptions
            {
                AddressPrefix = AdapterConfigReader.Read(
                    Config,
                    $"{prefix}.{AddressPrefixConfigurationKey}",
                    Options.AddressPrefix),
                Client = new MqttClientOptions
                {
                    Host = AdapterConfigReader.Read(Config, $"{prefix}.host", client.Host),
                    Port = AdapterConfigReader.Read(Config, $"{prefix}.port", client.Port),
                    ClientId = AdapterConfigReader.Read(Config, $"{prefix}.clientId", client.ClientId),
                    Username = AdapterConfigReader.Read<string?>(Config, $"{prefix}.username", client.Username),
                    Password = AdapterConfigReader.Read<string?>(Config, $"{prefix}.password", client.Password),
                    UseTls = AdapterConfigReader.Read(Config, $"{prefix}.useTls", client.UseTls),
                    KeepAliveSeconds = AdapterConfigReader.Read(Config, $"{prefix}.keepAliveSeconds", client.KeepAliveSeconds),
                    TopicFilter = AdapterConfigReader.Read(Config, $"{prefix}.topicFilter", client.TopicFilter)
                }
            };

            return base.OnInitializeAsync(cancellationToken);
        }

        public abstract override string Name { get; }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            _client = _clientFactory(Options.Client);
            _client.MessageReceived += OnMessageReceivedAsync;
            await _client.ConnectAsync(cancellationToken);
            await _client.SubscribeAsync(Options.Client.TopicFilter, cancellationToken);
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            if (_client is not null)
            {
                await _client.DisconnectAsync(cancellationToken);
                await _client.DisposeAsync();
                _client = null;
            }
        }

        protected override async Task OnSendPayloadAsync(BusFrame frame, CancellationToken cancellationToken)
        {
            if (_client is null || !_client.IsConnected)
            {
                return;
            }

            var topic = frame.Metadata.Values.TryGetValue("topic", out var metadataTopic)
                ? metadataTopic
                : frame.Address;
            await _client.PublishAsync(topic, frame.Payload, cancellationToken: cancellationToken);
        }

        protected override void OnReceivePayload(BusFrame frame)
        {
            _ = OnSendPayloadAsync(frame, CancellationToken.None);
        }

        protected virtual Task HandleIncomingMessageAsync(string topic, ReadOnlyMemory<byte> payload)
        {
            var address = string.IsNullOrWhiteSpace(Options.AddressPrefix)
                ? topic
                : $"{Options.AddressPrefix}.{topic.Replace('/', '.')}.value";

            PublishState(
                address,
                payload.ToArray(),
                "raw",
                new BusFrameMetadata { Values = { ["topic"] = topic } });
            return Task.CompletedTask;
        }

        private Task OnMessageReceivedAsync(string topic, ReadOnlyMemory<byte> payload)
        {
            return HandleIncomingMessageAsync(topic, payload);
        }
    }
}
