using System.Collections.Immutable;
using System.Diagnostics;

namespace VDict.Core.Policies;

/// <summary>
/// Runs at most one item and retains at most the newest pending item. Enqueueing a
/// newer item cooperatively cancels the active item and replaces work that has not
/// started yet.
/// </summary>
public sealed class LatestWinsWorkQueue<T> : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<T, CancellationToken, Task> _handler;
    private readonly List<double> _cancellationObservationMilliseconds = [];
    private WorkItem? _pending;
    private WorkItem? _activeItem;
    private CancellationTokenSource? _activeCancellation;
    private Task _worker = Task.CompletedTask;
    private bool _workerRunning;
    private bool _disposed;
    private int _activeCount;
    private int _peakActiveCount;

    public LatestWinsWorkQueue(Func<T, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public int PeakActiveCount => Volatile.Read(ref _peakActiveCount);

    public ImmutableArray<double> CancellationObservationMilliseconds
    {
        get
        {
            lock (_sync)
            {
                return [.. _cancellationObservationMilliseconds];
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending is null ? 0 : 1;
            }
        }
    }

    public Task EnqueueAsync(
        T value,
        CancellationToken cancellationToken = default)
    {
        WorkItem item = new(value, cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeItem?.MarkCancellationRequested();
            _activeCancellation?.Cancel();
            _pending?.CancelAsSuperseded();
            _pending = item;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _worker = Task.Run(ProcessAsync);
            }
        }

        return item.Completion.Task;
    }

    public void CancelCurrent()
    {
        lock (_sync)
        {
            _activeItem?.MarkCancellationRequested();
            _activeCancellation?.Cancel();
            _pending?.CancelAsSuperseded();
            _pending = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeItem?.MarkCancellationRequested();
            _activeCancellation?.Cancel();
            _pending?.CancelAsSuperseded();
            _pending = null;
            worker = _worker;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch
        {
            // Individual item failures are delivered through their completion task.
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            WorkItem? item;
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                item = _pending;
                _pending = null;
                if (item is null)
                {
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                    _workerRunning = false;
                    return;
                }

                cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    item.CancellationToken);
                _activeItem = item;
                _activeCancellation = cancellation;
            }

            int active = Interlocked.Increment(ref _activeCount);
            UpdatePeak(active);
            try
            {
                await _handler(item.Value, cancellation.Token).ConfigureAwait(false);
                item.Completion.TrySetResult();
            }
            catch (OperationCanceledException)
                when (cancellation.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(cancellation.Token);
            }
            catch (Exception exception)
            {
                item.Completion.TrySetException(exception);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                lock (_sync)
                {
                    double? cancellationLatency =
                        item.GetCancellationObservationMilliseconds();
                    if (cancellationLatency.HasValue)
                    {
                        _cancellationObservationMilliseconds.Add(
                            cancellationLatency.Value);
                    }

                    if (ReferenceEquals(_activeItem, item))
                    {
                        _activeItem = null;
                    }

                    if (ReferenceEquals(_activeCancellation, cancellation))
                    {
                        _activeCancellation = null;
                    }
                }

                cancellation.Dispose();
            }
        }
    }

    private void UpdatePeak(int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref _peakActiveCount)) &&
               Interlocked.CompareExchange(ref _peakActiveCount, value, current) != current)
        {
        }
    }

    private sealed class WorkItem(T value, CancellationToken cancellationToken)
    {
        private long _cancellationRequestedTimestamp;

        public T Value { get; } = value;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CancelAsSuperseded() =>
            Completion.TrySetCanceled(new CancellationToken(canceled: true));

        public void MarkCancellationRequested() =>
            Interlocked.CompareExchange(
                ref _cancellationRequestedTimestamp,
                Stopwatch.GetTimestamp(),
                0);

        public double? GetCancellationObservationMilliseconds()
        {
            long requested = Interlocked.Read(ref _cancellationRequestedTimestamp);
            return requested == 0
                ? null
                : Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
        }
    }
}
