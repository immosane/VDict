namespace VDict.Core.Policies;

public enum NavigationCompletionDisposition
{
    Ignored,
    Succeeded,
    Failed
}

public sealed class ReaderNavigationState
{
    private readonly object _sync = new();
    private long _latestVersion;
    private long _lastRenderedVersion;
    private ulong? _currentNavigationId;

    public long LatestVersion
    {
        get
        {
            lock (_sync)
            {
                return _latestVersion;
            }
        }
    }

    // The version claimed immediately before navigation is submitted. This is
    // duplicate-prevention state, not a completed-navigation identifier.
    public long LastRenderedVersion
    {
        get
        {
            lock (_sync)
            {
                return _lastRenderedVersion;
            }
        }
    }

    public bool HasContentError { get; private set; }

    public long RegisterDocument()
    {
        lock (_sync)
        {
            return ++_latestVersion;
        }
    }

    public bool ShouldRender(long version)
    {
        lock (_sync)
        {
            return version == _latestVersion &&
                version != _lastRenderedVersion;
        }
    }

    public bool TryBeginRender(long version)
    {
        lock (_sync)
        {
            if (version != _latestVersion ||
                version == _lastRenderedVersion)
            {
                return false;
            }

            _lastRenderedVersion = version;
            return true;
        }
    }

    public void NavigationStarted(ulong navigationId)
    {
        lock (_sync)
        {
            _currentNavigationId = navigationId;
        }
    }

    public NavigationCompletionDisposition NavigationCompleted(
        ulong navigationId,
        bool isSuccess)
    {
        lock (_sync)
        {
            if (_currentNavigationId != navigationId)
            {
                return NavigationCompletionDisposition.Ignored;
            }

            HasContentError = !isSuccess;
            return isSuccess
                ? NavigationCompletionDisposition.Succeeded
                : NavigationCompletionDisposition.Failed;
        }
    }
}
