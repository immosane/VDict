using Microsoft.UI.Windowing;

namespace VDict.WinUI.Platform;

internal static class WindowIcon
{
    public static void Apply(AppWindow window)
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "app.ico");
        if (File.Exists(iconPath))
        {
            window.SetIcon(iconPath);
        }
    }
}
