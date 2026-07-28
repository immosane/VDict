using VDict.Core.Environment;

namespace VDict.Core.Tests.Policies;

public sealed class DpiBoundsConverterTests
{
    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    public void AlignInside_UsesOnePhysicalCoordinateSpace(double scale)
    {
        const double x = 311.3333333333333;
        const double y = 149.3333333333333;
        const double width = 830.6666666666667;
        const double height = 547.3333333333334;

        PhysicalPixelBounds result = DpiBoundsConverter.AlignInside(
            x,
            y,
            width,
            height,
            scale);

        Assert.Equal(Math.Truncate(result.X), result.X);
        Assert.Equal(Math.Truncate(result.Y), result.Y);
        Assert.Equal(Math.Truncate(result.Width), result.Width);
        Assert.Equal(Math.Truncate(result.Height), result.Height);
        Assert.True(result.X >= x * scale - 0.001);
        Assert.True(result.Y >= y * scale - 0.001);
        Assert.True(result.X + result.Width <= (x + width) * scale + 0.001);
        Assert.True(result.Y + result.Height <= (y + height) * scale + 0.001);
        Assert.InRange(result.X - x * scale, -0.001, 1.0);
        Assert.InRange(result.Y - y * scale, -0.001, 1.0);
        Assert.InRange((x + width) * scale - result.X - result.Width, -0.001, 1.0);
        Assert.InRange((y + height) * scale - result.Y - result.Height, -0.001, 1.0);
    }
}
