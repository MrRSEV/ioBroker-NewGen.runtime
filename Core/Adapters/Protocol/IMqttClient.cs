using System;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    public interface IMqttClient : IAsyncDisposable
    {
        bool IsConnected { get; }

        event Func<string, ReadOnlyMemory<byte>, Task>? MessageReceived;

        Task ConnectAsync(CancellationToken cancellationToken = default);

        Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default);

        Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, bool retain = false, CancellationToken cancellationToken = default);

        Task DisconnectAsync(CancellationToken cancellationToken = default);
    }
}
