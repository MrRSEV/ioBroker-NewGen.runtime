using System;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters.Protocol
{
    /// <summary>
    /// Gemeinsamer Lifecycle für HTTP-basierte native Adapter. Der erste Poll
    /// läuft synchron im Start, weitere Polls werden nicht überlappend ausgeführt.
    /// </summary>
    public abstract class HttpPollingAdapterBase : DeviceAdapterBase
    {
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;

        protected HttpPollingAdapterBase(HttpClient? httpClient = null)
        {
            HttpClient = httpClient ?? new HttpClient();
        }

        protected HttpClient HttpClient { get; }

        protected TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Context.RuntimeContext.Logger.LogWarning($"[{Name}] Initialer Poll fehlgeschlagen; nächster Versuch folgt: {ex.Message}");
            }

            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollTask = PollLoopAsync(_pollCts.Token);
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            _pollCts?.Cancel();
            if (_pollTask is not null)
            {
                try
                {
                    await _pollTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _pollTask = null;
            _pollCts?.Dispose();
            _pollCts = null;
        }

        protected abstract Task PollOnceAsync(CancellationToken cancellationToken);

        private async Task PollLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(PollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await PollOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Context.RuntimeContext.Logger.LogWarning($"[{Name}] Poll fehlgeschlagen: {ex.Message}");
                }
            }
        }
    }
}
