namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    public sealed class MqttClientOptions
    {
        public string Host { get; init; } = "127.0.0.1";

        public int Port { get; init; } = 1883;

        public string ClientId { get; init; } = "newgen-client";

        public string? Username { get; init; }

        public string? Password { get; init; }

        public bool UseTls { get; init; }

        public int KeepAliveSeconds { get; init; } = 30;

        public string TopicFilter { get; init; } = "#";
    }
}
