using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

namespace VDict.WinUI.Platform;

internal static class WindowThemeController
{
    private static readonly Color DarkSurface = Color.FromArgb(255, 32, 32, 32);
    private static readonly Color LightSurface = Color.FromArgb(255, 243, 243, 243);
    private static readonly Color DarkText = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color LightText = Color.FromArgb(255, 31, 31, 31);

    public static void Apply(
        AppWindowTitleBar titleBar,
        bool dark,
        bool transparentBackground)
    {
        Color foreground = dark ? DarkText : LightText;
        Color inactiveForeground = dark
            ? Color.FromArgb(255, 160, 160, 160)
            : Color.FromArgb(255, 112, 112, 112);
        Color hoverBackground = dark
            ? Color.FromArgb(255, 62, 62, 62)
            : Color.FromArgb(255, 224, 224, 224);
        Color pressedBackground = dark
            ? Color.FromArgb(255, 82, 82, 82)
            : Color.FromArgb(255, 204, 204, 204);
        Color surface = dark ? DarkSurface : LightSurface;
        Color buttonBackground = transparentBackground
            ? Colors.Transparent
            : surface;

        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.BackgroundColor = transparentBackground
            ? Colors.Transparent
            : surface;
        titleBar.InactiveBackgroundColor = titleBar.BackgroundColor;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = buttonBackground;
        titleBar.ButtonInactiveBackgroundColor = buttonBackground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }
}
