using System.Text;
using System.Threading.Channels;
using VDict.Core.Environment;

namespace VDict.Infrastructure.Environment;

public sealed record SessionLogOptions(
    TimeSpan Retention,
    long MaximumTotalBytes,
    long MaximumSessionBytes)
{
    public static SessionLogOptions Default { get; } = new(
        TimeSpan.FromDays(14),
        32L * 1024 * 1024,
        8L * 1024 * 1024);
}

public sealed class SessionLog : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private readonly IAppDataPaths _paths;
    private readonly Channel<string> _lines;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private readonly SessionLogOptions _options;
    private long _acceptedBytes;
    private int _disposed;

    public SessionLog(IAppDataPaths paths, SessionLogOptions? options = null)
    {
        _paths = paths;
        _options = options ?? SessionLogOptions.Default;
        if (_options.Retention < TimeSpan.Zero ||
            _options.MaximumTotalBytes <= 0 ||
            _options.MaximumSessionBytes <= 0 ||
            _options.MaximumSessionBytes > _options.MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "日志保留期和容量上限必须为有效正值。");
        }

        FileName = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{System.Environment.ProcessId}.log";
        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        _pump = Task.Run(PumpAsync);
    }

    public string FileName { get; }

    public void Append(string line)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        long bytes = Encoding.UTF8.GetByteCount(line) + 2;
        long accepted = Interlocked.Add(ref _acceptedBytes, bytes);
        if (accepted > _options.MaximumSessionBytes ||
            !_lines.Writer.TryWrite(line))
        {
            Interlocked.Add(ref _acceptedBytes, -bytes);
        }
    }

    public void Dispose()
    {
        try
        {
            DisposeAsync().AsTask().Wait(ShutdownTimeout);
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lines.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _shutdown.Cancel();
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task PumpAsync()
    {
        StreamWriter? writer = null;
        try
        {
            await foreach (string line in _lines.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                try
                {
                    writer ??= CreateWriter();
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private StreamWriter CreateWriter()
    {
        _paths.EnsureCreated();
        Rotate();
        string path = Path.Combine(_paths.LogsDirectory, FileName);
        FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            useAsync: true);
        return new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private void Rotate()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - _options.Retention;
            FileInfo[] files = new DirectoryInfo(_paths.LogsDirectory)
                .EnumerateFiles("session-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            long retainedBytes = 0;
            long existingBudget = Math.Max(
                0,
                _options.MaximumTotalBytes - _options.MaximumSessionBytes);
            foreach (FileInfo file in files)
            {
                if (file.LastWriteTimeUtc < cutoff ||
                    retainedBytes + file.Length > existingBudget)
                {
                    file.Delete();
                    continue;
                }

                retainedBytes += file.Length;
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
