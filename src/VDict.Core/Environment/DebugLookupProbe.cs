using System.Collections.Immutable;
using System.Diagnostics;

namespace VDict.Core.Environment;

#if DEBUG
public sealed record DebugTimelineEvent(
    long Sequence,
    DateTimeOffset LocalTime,
    long MonotonicTimestamp,
    long InteractionId,
    string TriggerSource,
    string Stage,
    long RequestId,
    string RenderToken,
    int ThreadId,
    double ElapsedMilliseconds,
    double IntervalMilliseconds,
    string Detail);
#endif

/// <summary>
/// Debug-only lookup/render probe. Set VDICT_DEBUG_BREAK_STAGE to a stage name
/// (or "*") and attach a managed debugger to break at that boundary.
/// </summary>
public static class DebugLookupProbe
{
#if DEBUG
    private const int TimelineCapacity = 500;
    private const int CorrelationCapacity = 256;
    private static readonly object TimelineGate = new();
    private static readonly AsyncLocal<long?> CurrentInteraction = new();
    private static readonly Queue<DebugTimelineEvent> Timeline = new(TimelineCapacity);
    private static readonly Dictionary<long, CorrelationState> Correlations = [];
    private static readonly Dictionary<long, long> RequestInteractions = [];
    private static readonly Dictionary<string, long> RenderInteractions =
        new(StringComparer.Ordinal);
    private static readonly DateTimeOffset ClockAnchor = DateTimeOffset.Now;
    private static readonly long StopwatchAnchor = Stopwatch.GetTimestamp();
    private static long _nextSequence;
    private static long _nextInteraction;

    public static IDisposable BeginInteraction(string source, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        long interactionId = Interlocked.Increment(ref _nextInteraction);
        long? previous = CurrentInteraction.Value;
        CurrentInteraction.Value = interactionId;
        RecordCore(
            "Input",
            requestId: 0,
            renderToken: null,
            detail,
            source,
            interactionId);
        return new InteractionScope(previous);
    }

    public static ImmutableArray<DebugTimelineEvent> SnapshotAfter(long sequence)
    {
        lock (TimelineGate)
        {
            return Timeline
                .Where(item => item.Sequence > sequence)
                .ToImmutableArray();
        }
    }

    public static void ClearTimeline()
    {
        lock (TimelineGate)
        {
            Timeline.Clear();
            Correlations.Clear();
            RequestInteractions.Clear();
            RenderInteractions.Clear();
            CurrentInteraction.Value = null;
        }
    }

    public static int TimelineCount
    {
        get
        {
            lock (TimelineGate)
            {
                return Timeline.Count;
            }
        }
    }
#endif

    [Conditional("DEBUG")]
    public static void Hit(
        IAppLogger? logger,
        string stage,
        long requestId = 0,
        string? renderToken = null,
        string? detail = null,
        string? source = null)
    {
#if DEBUG
        RecordCore(stage, requestId, renderToken, detail, source);
        string render = string.IsNullOrEmpty(renderToken)
            ? "none"
            : renderToken[..Math.Min(8, renderToken.Length)];
        string message =
            $"Stage={stage}; RequestId={requestId}; Render={render}; " +
            $"Timestamp={Stopwatch.GetTimestamp()}; " +
            $"Thread={System.Environment.CurrentManagedThreadId}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; {detail}");
        Debug.WriteLine($"[VDict lookup probe] {message}");
        logger?.Write(AppLogLevel.Information, "debug-lookup-probe", message);

        string? breakStage =
            System.Environment.GetEnvironmentVariable("VDICT_DEBUG_BREAK_STAGE");
        if (Debugger.IsAttached &&
            (string.Equals(breakStage, "*", StringComparison.Ordinal) ||
             string.Equals(breakStage, stage, StringComparison.OrdinalIgnoreCase)))
        {
            Debugger.Break();
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void Record(
        string stage,
        long requestId = 0,
        string? renderToken = null,
        string? detail = null,
        string? source = null)
    {
#if DEBUG
        RecordCore(stage, requestId, renderToken, detail, source);
#endif
    }

#if DEBUG
    private static void RecordCore(
        string stage,
        long requestId,
        string? renderToken,
        string? detail,
        string? source,
        long? forcedInteractionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        long timestamp = Stopwatch.GetTimestamp();
        string render = string.IsNullOrEmpty(renderToken)
            ? string.Empty
            : renderToken[..Math.Min(8, renderToken.Length)];

        lock (TimelineGate)
        {
            long interactionId = ResolveInteraction(
                forcedInteractionId,
                requestId,
                render,
                source,
                timestamp);
            CorrelationState correlation = Correlations[interactionId];
            if (requestId > 0)
            {
                RequestInteractions[requestId] = interactionId;
                correlation.RequestIds.Add(requestId);
            }

            if (render.Length > 0)
            {
                RenderInteractions[render] = interactionId;
                correlation.RenderTokens.Add(render);
            }

            double elapsed = Stopwatch.GetElapsedTime(
                correlation.StartedTimestamp,
                timestamp).TotalMilliseconds;
            double interval = Stopwatch.GetElapsedTime(
                correlation.LastTimestamp,
                timestamp).TotalMilliseconds;
            correlation.LastTimestamp = timestamp;
            DebugTimelineEvent item = new(
                Interlocked.Increment(ref _nextSequence),
                ClockAnchor + Stopwatch.GetElapsedTime(StopwatchAnchor, timestamp),
                timestamp,
                interactionId,
                correlation.Source,
                stage,
                requestId,
                render,
                System.Environment.CurrentManagedThreadId,
                elapsed,
                interval,
                detail ?? string.Empty);
            Timeline.Enqueue(item);
            while (Timeline.Count > TimelineCapacity)
            {
                Timeline.Dequeue();
            }

            TrimCorrelations();
        }
    }

    private static long ResolveInteraction(
        long? forcedInteractionId,
        long requestId,
        string renderToken,
        string? source,
        long timestamp)
    {
        long? interactionId = forcedInteractionId ?? CurrentInteraction.Value;
        if (interactionId is null &&
            requestId > 0 &&
            RequestInteractions.TryGetValue(requestId, out long requestInteraction))
        {
            interactionId = requestInteraction;
        }

        if (interactionId is null &&
            renderToken.Length > 0 &&
            RenderInteractions.TryGetValue(renderToken, out long renderInteraction))
        {
            interactionId = renderInteraction;
        }

        if (interactionId is null)
        {
            interactionId = Interlocked.Increment(ref _nextInteraction);
        }

        if (!Correlations.TryGetValue(interactionId.Value, out CorrelationState? correlation))
        {
            correlation = new CorrelationState(
                string.IsNullOrWhiteSpace(source) ? "Background" : source,
                timestamp);
            Correlations.Add(interactionId.Value, correlation);
        }

        return interactionId.Value;
    }

    private static void TrimCorrelations()
    {
        if (Correlations.Count <= CorrelationCapacity)
        {
            return;
        }

        HashSet<long> retained = Timeline
            .Select(item => item.InteractionId)
            .ToHashSet();
        long[] stale = Correlations.Keys
            .Where(interactionId => !retained.Contains(interactionId))
            .ToArray();
        foreach (long interactionId in stale)
        {
            RemoveCorrelation(interactionId);
        }

        while (Correlations.Count > CorrelationCapacity)
        {
            long oldest = Correlations
                .OrderBy(pair => pair.Value.StartedTimestamp)
                .First()
                .Key;
            RemoveCorrelation(oldest);
        }
    }

    private static void RemoveCorrelation(long interactionId)
    {
        if (!Correlations.Remove(interactionId, out CorrelationState? correlation))
        {
            return;
        }

        foreach (long requestId in correlation.RequestIds)
        {
            RequestInteractions.Remove(requestId);
        }

        foreach (string renderToken in correlation.RenderTokens)
        {
            RenderInteractions.Remove(renderToken);
        }
    }

    private sealed class InteractionScope(long? previous) : IDisposable
    {
        private long? _previous = previous;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CurrentInteraction.Value = _previous;
            _previous = null;
        }
    }

    private sealed class CorrelationState(
        string source,
        long startedTimestamp)
    {
        public string Source { get; } = source;

        public long StartedTimestamp { get; } = startedTimestamp;

        public long LastTimestamp { get; set; } = startedTimestamp;

        public HashSet<long> RequestIds { get; } = [];

        public HashSet<string> RenderTokens { get; } = new(StringComparer.Ordinal);
    }
#endif
}
