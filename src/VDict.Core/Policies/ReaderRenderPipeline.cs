namespace VDict.Core.Policies;

public enum ReaderRenderStage
{
    NavigationGateWait,
    NavigationGateAcquired,
    UnavailableBeforeStop,
    StopMediaStart,
    StopMediaComplete,
    CancelledAfterStop,
    UnavailableAfterStop,
    StaleOrDuplicateAfterStop,
    RenderClaimed,
    NavigateSubmitted
}

public enum ReaderRenderDisposition
{
    NavigateSubmitted,
    UnavailableBeforeStop,
    UnavailableAfterStop,
    StaleOrDuplicateAfterStop
}

/// <summary>
/// Serializes the awaitable portion of reader rendering. A version is claimed only
/// after the final await, so cancellation or a newer document cannot consume it.
/// The commit callback must remain synchronous and submit navigation before it returns.
/// </summary>
public static class ReaderRenderPipeline
{
    public static async Task<ReaderRenderDisposition> ExecuteAsync(
        long version,
        ReaderNavigationState navigationState,
        SemaphoreSlim navigationGate,
        Func<bool> canRender,
        Func<Task> stopMediaAsync,
        Action commitNavigation,
        Action<ReaderRenderStage>? trace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigationState);
        ArgumentNullException.ThrowIfNull(navigationGate);
        ArgumentNullException.ThrowIfNull(canRender);
        ArgumentNullException.ThrowIfNull(stopMediaAsync);
        ArgumentNullException.ThrowIfNull(commitNavigation);

        trace?.Invoke(ReaderRenderStage.NavigationGateWait);
        await navigationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            trace?.Invoke(ReaderRenderStage.NavigationGateAcquired);
            if (!canRender())
            {
                trace?.Invoke(ReaderRenderStage.UnavailableBeforeStop);
                return ReaderRenderDisposition.UnavailableBeforeStop;
            }

            trace?.Invoke(ReaderRenderStage.StopMediaStart);
            await stopMediaAsync().ConfigureAwait(true);
            trace?.Invoke(ReaderRenderStage.StopMediaComplete);

            if (cancellationToken.IsCancellationRequested)
            {
                trace?.Invoke(ReaderRenderStage.CancelledAfterStop);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (!canRender())
            {
                trace?.Invoke(ReaderRenderStage.UnavailableAfterStop);
                return ReaderRenderDisposition.UnavailableAfterStop;
            }

            // This is deliberately after the final await. TryBeginRender performs
            // the latest-version and duplicate checks atomically with the claim.
            if (!navigationState.TryBeginRender(version))
            {
                trace?.Invoke(ReaderRenderStage.StaleOrDuplicateAfterStop);
                return ReaderRenderDisposition.StaleOrDuplicateAfterStop;
            }

            trace?.Invoke(ReaderRenderStage.RenderClaimed);
            commitNavigation();
            trace?.Invoke(ReaderRenderStage.NavigateSubmitted);
            return ReaderRenderDisposition.NavigateSubmitted;
        }
        finally
        {
            navigationGate.Release();
        }
    }
}
