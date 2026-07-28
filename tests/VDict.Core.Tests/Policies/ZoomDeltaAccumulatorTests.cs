using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class ZoomDeltaAccumulatorTests
{
    [Fact]
    public void HighPrecisionWheelDelta_AccumulatesIntoTenthSteps()
    {
        ZoomDeltaAccumulator accumulator = new();

        Assert.Equal(0, accumulator.AddWheelDelta(30));
        Assert.Equal(0, accumulator.AddWheelDelta(30));
        Assert.Equal(-1, accumulator.AddWheelDelta(40));
        Assert.Equal(2, accumulator.AddWheelDelta(-240));
    }

    [Fact]
    public void InvalidDelta_IsIgnoredAndResetDropsRemainder()
    {
        ZoomDeltaAccumulator accumulator = new();

        Assert.Equal(0, accumulator.AddWheelDelta(double.NaN));
        Assert.Equal(0, accumulator.AddWheelDelta(60));
        accumulator.Reset();
        Assert.Equal(0, accumulator.AddWheelDelta(40));
    }
}
