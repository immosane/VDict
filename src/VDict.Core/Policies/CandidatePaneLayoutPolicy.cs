namespace VDict.Core.Policies;

public static class CandidatePaneLayoutPolicy
{
    public const double MinimumWidth = 180;

    public const double MaximumWidth = 380;

    public const double NarrowPresetWidth = 220;

    public const double WidePresetWidth = 280;

    public const double WideWindowThreshold = 1040;

    public static double GetResponsivePreset(double windowWidth) =>
        windowWidth >= WideWindowThreshold
            ? WidePresetWidth
            : NarrowPresetWidth;

    public static double GetMaximumDisplayedWidth(
        double contentWidth,
        double dividerWidth,
        double minimumReaderWidth)
    {
        double available = contentWidth - dividerWidth - minimumReaderWidth;
        return Math.Clamp(available, MinimumWidth, MaximumWidth);
    }

    public static double ResolveDisplayedWidth(
        double? preferredWidth,
        double windowWidth,
        double contentWidth,
        double dividerWidth,
        double minimumReaderWidth)
    {
        double requested = preferredWidth ?? GetResponsivePreset(windowWidth);
        return Math.Clamp(
            requested,
            MinimumWidth,
            GetMaximumDisplayedWidth(contentWidth, dividerWidth, minimumReaderWidth));
    }
}
