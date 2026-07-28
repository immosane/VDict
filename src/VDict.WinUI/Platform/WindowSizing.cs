using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace VDict.WinUI.Platform;

internal static partial class WindowSizing
{
    private const uint DefaultDpi = 96;

    public static void ResizeToWorkArea(
        AppWindow window,
        nint hwnd,
        double preferredWidthDip,
        double preferredHeightDip,
        double minimumWidthDip,
        double minimumHeightDip)
    {
        double scale = GetScale(hwnd);
        DisplayArea area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        int maximumWidth = Math.Max(1, area.WorkArea.Width);
        int maximumHeight = Math.Max(1, area.WorkArea.Height);
        int width = Math.Min(maximumWidth, DipToPixels(preferredWidthDip, scale));
        int height = Math.Min(maximumHeight, DipToPixels(preferredHeightDip, scale));
        width = Math.Min(maximumWidth, Math.Max(width, DipToPixels(minimumWidthDip, scale)));
        height = Math.Min(maximumHeight, Math.Max(height, DipToPixels(minimumHeightDip, scale)));
        window.Resize(new SizeInt32(width, height));
    }

    public static bool EnsureMinimumSize(
        AppWindow window,
        nint hwnd,
        double minimumWidthDip,
        double minimumHeightDip)
    {
        double scale = GetScale(hwnd);
        int minimumWidth = DipToPixels(minimumWidthDip, scale);
        int minimumHeight = DipToPixels(minimumHeightDip, scale);
        SizeInt32 size = window.Size;
        int width = Math.Max(size.Width, minimumWidth);
        int height = Math.Max(size.Height, minimumHeight);
        if (width == size.Width && height == size.Height)
        {
            return false;
        }

        DisplayArea area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        window.Resize(new SizeInt32(
            Math.Min(width, area.WorkArea.Width),
            Math.Min(height, area.WorkArea.Height)));
        return true;
    }

    public static double PixelsToDip(nint hwnd, int pixels) =>
        pixels / GetScale(hwnd);

    private static double GetScale(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return (dpi == 0 ? DefaultDpi : dpi) / (double)DefaultDpi;
    }

    private static int DipToPixels(double dip, double scale) =>
        Math.Max(1, (int)Math.Round(dip * scale));

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
