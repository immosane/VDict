namespace VDict.Core.Environment;

public readonly record struct PhysicalPixelBounds(
    double X,
    double Y,
    double Width,
    double Height);

public static class DpiBoundsConverter
{
    public static PhysicalPixelBounds AlignInside(
        double x,
        double y,
        double width,
        double height,
        double rasterizationScale)
    {
        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            !double.IsFinite(rasterizationScale) ||
            width < 0 ||
            height < 0 ||
            rasterizationScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rasterizationScale),
                "DIP bounds and rasterization scale must be finite and non-negative.");
        }

        // WinUI layout commonly produces repeating thirds at 150% DPI. Values
        // within one thousandth of a physical pixel represent the same edge.
        const double epsilon = 0.001;
        double leftPixels = Math.Ceiling(x * rasterizationScale - epsilon);
        double topPixels = Math.Ceiling(y * rasterizationScale - epsilon);
        double rightPixels = Math.Floor((x + width) * rasterizationScale + epsilon);
        double bottomPixels = Math.Floor((y + height) * rasterizationScale + epsilon);
        return new PhysicalPixelBounds(
            leftPixels,
            topPixels,
            Math.Max(0, rightPixels - leftPixels),
            Math.Max(0, bottomPixels - topPixels));
    }
}
