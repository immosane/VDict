#if DEBUG
using Microsoft.UI.Windowing;
using VDict.Core.Localization;
using VDict.Presentation.Services;
using VDict.WinUI.Views;
using WinRT.Interop;

namespace VDict.WinUI.Diagnostics;

public sealed class DebugTimelineWindowController(
    IClipboardService clipboard,
    IAppLocalizer localizer)
{
    private DebugTimelineWindow? _window;

    public void Show(MainWindow owner)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        DebugTimelineWindow window = new(clipboard, localizer);
        window.WindowClosed += Window_WindowClosed;
        _window = window;
        window.Activate();
        nint ownerHwnd = WindowNative.GetWindowHandle(owner);
        Microsoft.UI.WindowId ownerId =
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd);
        window.PositionBeside(AppWindow.GetFromWindowId(ownerId));
    }

    public void Toggle(MainWindow owner)
    {
        if (_window is null)
        {
            Show(owner);
        }
        else
        {
            Close();
        }
    }

    public void Close()
    {
        DebugTimelineWindow? window = _window;
        if (window is null)
        {
            return;
        }

        window.WindowClosed -= Window_WindowClosed;
        _window = null;
        window.Close();
    }

    private void Window_WindowClosed(object? sender, EventArgs e)
    {
        if (sender is DebugTimelineWindow window)
        {
            window.WindowClosed -= Window_WindowClosed;
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }
    }
}
#endif
