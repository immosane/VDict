#if DEBUG
using VDict.Core.Environment;

namespace VDict.Core.Tests.Environment;

public sealed class DebugLookupProbeTests : IDisposable
{
    public DebugLookupProbeTests() => DebugLookupProbe.ClearTimeline();

    public void Dispose() => DebugLookupProbe.ClearTimeline();

    [Fact]
    public void Interaction_CorrelatesRequestAndRenderStages()
    {
        using (DebugLookupProbe.BeginInteraction(
                   "SearchButton",
                   "QueryLength=7"))
        {
            DebugLookupProbe.Hit(
                logger: null,
                "LookupStart",
                requestId: 42,
                detail: "Origin=Manual; QueryLength=7");
            DebugLookupProbe.Hit(
                logger: null,
                "LookupDataReady",
                requestId: 42,
                renderToken: "0123456789abcdef",
                detail: "Results=1");
        }

        DebugLookupProbe.Record(
            "NavigationCompleted",
            renderToken: "0123456789abcdef");

        var events = DebugLookupProbe.SnapshotAfter(0);

        Assert.Equal(4, events.Length);
        Assert.Equal(
            ["Input", "LookupStart", "LookupDataReady", "NavigationCompleted"],
            events.Select(item => item.Stage).ToArray());
        Assert.Single(events.Select(item => item.InteractionId).Distinct());
        Assert.All(events, item => Assert.Equal("SearchButton", item.TriggerSource));
        Assert.All(events, item => Assert.True(item.ElapsedMilliseconds >= 0));
        Assert.All(events, item => Assert.True(item.IntervalMilliseconds >= 0));
        Assert.Equal("01234567", events[^1].RenderToken);
        Assert.True(events.Zip(events.Skip(1)).All(pair =>
            pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public void Timeline_IsBoundedAndCanBeCleared()
    {
        for (int index = 0; index < 650; index++)
        {
            DebugLookupProbe.Record("Stage", detail: $"Index={index}");
        }

        var events = DebugLookupProbe.SnapshotAfter(0);

        Assert.Equal(500, events.Length);
        Assert.Equal(500, DebugLookupProbe.TimelineCount);
        Assert.Contains("Index=649", events[^1].Detail);

        DebugLookupProbe.ClearTimeline();

        Assert.Empty(DebugLookupProbe.SnapshotAfter(0));
        Assert.Equal(0, DebugLookupProbe.TimelineCount);
    }

    [Fact]
    public void ConcurrentWriters_ProduceUniqueOrderedSequences()
    {
        Parallel.For(0, 200, index =>
            DebugLookupProbe.Record("Concurrent", detail: $"Index={index}"));

        var events = DebugLookupProbe.SnapshotAfter(0);
        long[] sequences = events.Select(item => item.Sequence).ToArray();

        Assert.Equal(200, events.Length);
        Assert.Equal(200, sequences.Distinct().Count());
        Assert.Equal(sequences.Order().ToArray(), sequences);
    }

    [Fact]
    public void TimelineDetails_DoNotRequireQueryText()
    {
        const string privateQuery = "private-query";
        using (DebugLookupProbe.BeginInteraction(
                   "CandidateClick",
                   $"QueryLength={privateQuery.Length}"))
        {
            DebugLookupProbe.Record(
                "LookupStart",
                requestId: 1,
                detail: $"Origin=Candidate; QueryLength={privateQuery.Length}");
        }

        string combined = string.Join(
            System.Environment.NewLine,
            DebugLookupProbe.SnapshotAfter(0).Select(item => item.Detail));

        Assert.DoesNotContain(privateQuery, combined, StringComparison.Ordinal);
        Assert.Contains("QueryLength=13", combined, StringComparison.Ordinal);
    }
}
#endif
