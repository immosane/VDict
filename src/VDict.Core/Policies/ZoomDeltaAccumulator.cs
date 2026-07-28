namespace VDict.Core.Policies;

public sealed class ZoomDeltaAccumulator(double threshold = 100)
{
    private readonly double _threshold = threshold > 0
        ? threshold
        : throw new ArgumentOutOfRangeException(nameof(threshold));
    private double _accumulated;

    public int AddWheelDelta(double deltaY)
    {
        if (!double.IsFinite(deltaY))
        {
            return 0;
        }

        _accumulated -= Math.Clamp(deltaY, -1000, 1000);
        int steps = (int)(_accumulated / _threshold);
        if (steps != 0)
        {
            _accumulated -= steps * _threshold;
        }

        return steps;
    }

    public void Reset() => _accumulated = 0;
}
