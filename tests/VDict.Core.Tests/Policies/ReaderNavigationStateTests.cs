using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class ReaderNavigationStateTests
{
    [Fact]
    public void SameVersionCanBeClaimedOnlyOnce()
    {
        ReaderNavigationState state = new();
        long version = state.RegisterDocument();

        Assert.True(state.TryBeginRender(version));

        // This is duplicate-prevention semantics. Production must not treat
        // ShouldRender as a continuation condition after a successful claim.
        Assert.False(state.ShouldRender(version));
        Assert.False(state.TryBeginRender(version));
        Assert.Equal(version, state.LastRenderedVersion);
    }

    [Fact]
    public void OldFailureThenLatestSuccessDoesNotShowError()
    {
        ReaderNavigationState state = new();
        state.NavigationStarted(10);
        state.NavigationStarted(11);

        Assert.Equal(
            NavigationCompletionDisposition.Ignored,
            state.NavigationCompleted(10, isSuccess: false));
        Assert.False(state.HasContentError);
        Assert.Equal(
            NavigationCompletionDisposition.Succeeded,
            state.NavigationCompleted(11, isSuccess: true));
        Assert.False(state.HasContentError);
    }

    [Fact]
    public void LatestNavigationFailureShowsError()
    {
        ReaderNavigationState state = new();
        state.NavigationStarted(20);

        Assert.Equal(
            NavigationCompletionDisposition.Failed,
            state.NavigationCompleted(20, isSuccess: false));
        Assert.True(state.HasContentError);
    }

    [Fact]
    public void SuccessfulNavigationClearsEarlierError()
    {
        ReaderNavigationState state = new();
        state.NavigationStarted(30);
        state.NavigationCompleted(30, isSuccess: false);
        state.NavigationStarted(31);

        Assert.Equal(
            NavigationCompletionDisposition.Succeeded,
            state.NavigationCompleted(31, isSuccess: true));
        Assert.False(state.HasContentError);
    }

    [Fact]
    public void RapidDocumentSubmissionsOnlyRenderLatestVersion()
    {
        ReaderNavigationState state = new();
        long first = state.RegisterDocument();
        long second = state.RegisterDocument();
        long third = state.RegisterDocument();

        Assert.False(state.ShouldRender(first));
        Assert.False(state.ShouldRender(second));
        Assert.True(state.ShouldRender(third));

        Assert.True(state.TryBeginRender(third));

        Assert.False(state.ShouldRender(third));
        Assert.Equal(third, state.LastRenderedVersion);
    }
}
