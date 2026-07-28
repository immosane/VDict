using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class CandidateAndHistoryTests
{
    [Fact]
    public void RequestGuard_RequiresEveryCapturedValueToRemainCurrent()
    {
        DictionaryId id = new("dict");

        Assert.True(CandidateRequestGuard.IsCurrent(4, 4, "Word", "word", id, id, 8, 8));
        Assert.False(CandidateRequestGuard.IsCurrent(3, 4, "Word", "word", id, id, 8, 8));
        Assert.False(CandidateRequestGuard.IsCurrent(4, 4, "Word", "other", id, id, 8, 8));
        Assert.False(CandidateRequestGuard.IsCurrent(4, 4, "Word", "word", id, null, 8, 8));
        Assert.False(CandidateRequestGuard.IsCurrent(4, 4, "Word", "word", id, id, 7, 8));
    }

    [Fact]
    public void ExplicitCandidate_IsTrimmedAndUsedForEnter()
    {
        CandidateSelectionState state = new();
        state.Mark("  chosen  ", CandidateSelectionOrigin.Keyboard);

        Assert.Equal("chosen", state.ResolveEnterQuery("typed"));

        state.Reset();
        Assert.Equal("typed", state.ResolveEnterQuery(" typed "));
    }

    [Theory]
    [InlineData(-1, 3, 1, 0)]
    [InlineData(2, 3, 1, 0)]
    [InlineData(-1, 3, -1, 2)]
    [InlineData(0, 3, -1, 2)]
    [InlineData(1, 3, 1, 2)]
    public void CandidateMovement_WrapsAtBothEnds(
        int current,
        int count,
        int direction,
        int expected)
    {
        Assert.Equal(
            expected,
            CandidateSelectionState.MoveIndex(current, count, direction));
    }

    [Fact]
    public void History_PreservesPathAndSupportsBackForward()
    {
        LookupHistory history = new(2);
        history.Record("one", ["root", "one"], "two");
        history.Record("two", ["root", "two"], "three");

        Assert.True(history.TryGoBack("three", ["root", "three"], out LookupHistoryEntry back));
        Assert.Equal("two", back.Word);
        Assert.Equal<string>(["root", "two"], back.Path);
        Assert.True(history.TryGoForward("two", ["root", "two"], out LookupHistoryEntry forward));
        Assert.Equal("three", forward.Word);
    }

    [Fact]
    public void History_CapturesAnImmutablePathSnapshot()
    {
        LookupHistory history = new(2);
        List<string> sourcePath = ["root", "one"];

        history.Record("one", sourcePath, "two");
        sourcePath[1] = "mutated";
        sourcePath.Add("extra");

        Assert.True(history.TryGoBack("two", ["root", "two"], out LookupHistoryEntry back));
        Assert.Equal<string>(["root", "one"], back.Path);
    }

    [Fact]
    public void History_ExplicitSnapshotAllowsSameWordPathNavigation()
    {
        LookupHistory history = new(4);

        history.RecordSnapshot("A", ["A", "B", "A"]);

        Assert.True(history.TryGoBack("A", ["A"], out LookupHistoryEntry back));
        Assert.Equal("A", back.Word);
        Assert.Equal<string>(["A", "B", "A"], back.Path);
    }

    [Fact]
    public void LookupPathState_HandlesEveryNavigationSource()
    {
        Assert.Equal<string>(
            ["manual"],
            LookupPathState.Update(["old"], "manual", LookupOrigin.Manual, true));
        Assert.Equal<string>(
            ["root", "candidate"],
            LookupPathState.Update(["root", "old"], "candidate", LookupOrigin.Candidate, false));
        Assert.Equal<string>(
            ["root", "entry"],
            LookupPathState.Update(["root"], "entry", LookupOrigin.Entry, false));
        Assert.Equal<string>(
            ["root", "selected"],
            LookupPathState.Update(["root"], "selected", LookupOrigin.SelectedText, false));
        Assert.Equal<string>(
            ["root"],
            LookupPathState.Update(["root"], "ignored", LookupOrigin.History, false));
        Assert.Equal<string>(
            ["root"],
            LookupPathState.Update(["root"], "ignored", LookupOrigin.Path, false));
        Assert.Equal<string>(
            ["root"],
            LookupPathState.Update(["root"], "ignored", LookupOrigin.ScopeRefresh, false));
    }

    [Fact]
    public void LookupPathState_DeduplicatesTailIgnoringCaseAndBuildsIndexedItems()
    {
        Assert.Equal<string>(
            ["Word"],
            LookupPathState.Update(["Word"], "word", LookupOrigin.Entry, false));

        var items = LookupPathState.CreateItems(["A", "B", "A"]);

        Assert.Equal([0, 1, 2], items.Select(item => item.Index));
        Assert.False(items[0].IsCurrent);
        Assert.True(items[2].IsCurrent);
        Assert.Contains("Current entry", items[2].AccessibleName);
    }

    [Fact]
    public void ContextSelection_ClearsStaleTextWheneverANewMenuIsBuilt()
    {
        ContextSelectionState state = new(8);

        Assert.True(state.BeginMenu(true, "  selected text  "));
        Assert.Equal("selected", state.Value);

        Assert.False(state.BeginMenu(false, null));
        Assert.Empty(state.Value);
    }

    [Fact]
    public void TrustedActivation_IsShortLivedAndSingleUse()
    {
        TrustedWebActivationGate gate = new(TimeSpan.FromSeconds(2));
        DateTimeOffset now = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

        gate.Record(
            "render-a",
            TrustedWebAction.ExternalLink,
            TrustedWebActivationSource.WebViewUserInitiated,
            utcNow: now);

        Assert.True(gate.TryConsume(
            "render-a",
            TrustedWebAction.ExternalLink,
            utcNow: now.AddSeconds(1)));
        Assert.False(gate.TryConsume(
            "render-a",
            TrustedWebAction.Download,
            utcNow: now.AddSeconds(1)));
        gate.Record(
            "render-a",
            TrustedWebAction.Download,
            TrustedWebActivationSource.HostConfirmation,
            utcNow: now);
        Assert.False(gate.TryConsume(
            "render-a",
            TrustedWebAction.Download,
            utcNow: now.AddSeconds(3)));

        gate.Record(
            "render-a",
            TrustedWebAction.Download,
            TrustedWebActivationSource.HostConfirmation,
            utcNow: now);
        Assert.False(gate.TryConsume(
            "render-a",
            TrustedWebAction.ExternalLink,
            utcNow: now));
    }

    [Fact]
    public void TrustedActivation_RejectsCrossSessionFrameReplayAndRevocation()
    {
        TrustedWebActivationGate gate = new();
        DateTimeOffset now = new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

        gate.Record(
            "render-a",
            TrustedWebAction.Download,
            TrustedWebActivationSource.HostConfirmation,
            "frame-a",
            now);
        Assert.False(gate.TryConsume(
            "render-b",
            TrustedWebAction.Download,
            "frame-a",
            now));

        gate.Record(
            "render-a",
            TrustedWebAction.Download,
            TrustedWebActivationSource.HostConfirmation,
            "frame-a",
            now);
        Assert.False(gate.TryConsume(
            "render-a",
            TrustedWebAction.Download,
            "frame-b",
            now));

        gate.Record(
            "render-a",
            TrustedWebAction.Download,
            TrustedWebActivationSource.HostConfirmation,
            utcNow: now);
        gate.Revoke();
        Assert.False(gate.TryConsume(
            "render-a",
            TrustedWebAction.Download,
            utcNow: now));
    }
}
