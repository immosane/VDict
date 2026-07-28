using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class CandidatePaneLayoutPolicyTests
{
    [Theory]
    [InlineData(820, 220)]
    [InlineData(1039, 220)]
    [InlineData(1040, 280)]
    [InlineData(1400, 280)]
    public void UnsetWidth_UsesResponsivePreset(double windowWidth, double expected)
    {
        double contentWidth = windowWidth - 24;

        double actual = CandidatePaneLayoutPolicy.ResolveDisplayedWidth(
            null,
            windowWidth,
            contentWidth,
            dividerWidth: 8,
            minimumReaderWidth: 480);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SavedWidth_IsOnlyClippedForTheCurrentNarrowWindow()
    {
        double narrow = CandidatePaneLayoutPolicy.ResolveDisplayedWidth(
            380,
            windowWidth: 820,
            contentWidth: 796,
            dividerWidth: 8,
            minimumReaderWidth: 480);
        double restored = CandidatePaneLayoutPolicy.ResolveDisplayedWidth(
            380,
            windowWidth: 1280,
            contentWidth: 1256,
            dividerWidth: 8,
            minimumReaderWidth: 480);

        Assert.Equal(308, narrow);
        Assert.Equal(380, restored);
    }

    [Theory]
    [InlineData(100, 180)]
    [InlineData(240, 240)]
    [InlineData(500, 380)]
    public void ExplicitWidth_StaysWithinInteractionBounds(
        double preferred,
        double expected)
    {
        double actual = CandidatePaneLayoutPolicy.ResolveDisplayedWidth(
            preferred,
            windowWidth: 1280,
            contentWidth: 1256,
            dividerWidth: 8,
            minimumReaderWidth: 480);

        Assert.Equal(expected, actual);
    }
}
