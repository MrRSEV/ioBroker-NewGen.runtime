using System;
using System.Threading;

namespace ioBroker_NewGen.Core.Matrix
{
    /// <summary>
    /// Hintergrundverarbeitung für semantische Objekt-Embeddings. Der Worker
    /// wird nicht vom SetState-Pfad aufgerufen; er verarbeitet ausschließlich
    /// als dirty markierte MatrixObjects.
    /// </summary>
    public sealed class SemanticEmbeddingWorker : IDisposable
    {
        private readonly SemanticStateMatrix _matrix;
        private readonly TimeSpan _interval;
        private readonly object _lifecycleLock = new();
        private Timer? _timer;
        private int _processing;

        public SemanticEmbeddingWorker(
            SemanticStateMatrix matrix,
            TimeSpan? interval = null)
        {
            _matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
            _interval = interval ?? TimeSpan.FromSeconds(30);
            if (_interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "Das Worker-Intervall muss positiv sein.");
            }
        }

        public bool IsRunning
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _timer is not null;
                }
            }
        }

        public TimeSpan Interval => _interval;

        /// <summary>
        /// Startet die periodische Verarbeitung. Die erste Verarbeitung läuft
        /// sofort, danach im konfigurierten Intervall.
        /// </summary>
        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_timer is not null)
                {
                    return;
                }

                _timer = new Timer(
                    ProcessCallback,
                    state: null,
                    dueTime: TimeSpan.Zero,
                    period: _interval);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        /// <summary>Verarbeitet ausstehende Objekte synchron für Tests/Flush.</summary>
        public int ProcessOnce(CancellationToken cancellationToken = default)
        {
            return ProcessIfIdle(cancellationToken);
        }

        private void ProcessCallback(object? state)
        {
            ProcessIfIdle(CancellationToken.None);
        }

        private int ProcessIfIdle(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _processing, 1) != 0)
            {
                return 0;
            }

            try
            {
                return _matrix.ProcessDirtySemanticObjects(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            finally
            {
                Volatile.Write(ref _processing, 0);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
