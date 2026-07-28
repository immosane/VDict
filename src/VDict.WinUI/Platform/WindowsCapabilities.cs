using Windows.UI.ViewManagement;

namespace VDict.WinUI.Platform;

public sealed class WindowsCapabilities
{
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();

    public bool IsWindows11OrLater =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public bool IsHighContrast => _accessibility.HighContrast;

    public bool AdvancedEffectsEnabled => _uiSettings.AdvancedEffectsEnabled;

    public bool CanUseMica =>
        IsWindows11OrLater && !IsHighContrast && AdvancedEffectsEnabled;
}
