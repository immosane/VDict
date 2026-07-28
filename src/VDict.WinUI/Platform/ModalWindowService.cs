using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Presentation.Services;
using VDict.Presentation.ViewModels;
using VDict.WinUI.Controls;
using VDict.WinUI.Views;
using WinRT.Interop;

namespace VDict.WinUI.Platform;

public sealed partial class ModalWindowService(
    IServiceScopeFactory scopeFactory,
    MainWindowAccessor mainWindowAccessor,
    ReaderWebViewHost readerHost,
    IAppLogger logger,
    IAppLocalizer localizer) : IModalWindowService
{
    private const int GwlpHwndParent = -8;
    private readonly object _sync = new();
    private SettingsWindow? _window;
    private SettingsWindowState _state = SettingsWindowState.Closed;

    public Task ShowSettingsAsync(CancellationToken cancellationToken = default)
    {
        Trace("ShowSettingsAsync entered");
        lock (_sync)
        {
            if (_state == SettingsWindowState.Open && _window is not null)
            {
                _window.Activate();
                return Task.CompletedTask;
            }

            if (_state is SettingsWindowState.Opening or
                SettingsWindowState.Saving or
                SettingsWindowState.Closing)
            {
                return Task.CompletedTask;
            }

            _state = SettingsWindowState.Opening;
        }

        Trace("dispatching ShowCoreAsync");
        return ShowCoreAsync(cancellationToken);
    }

    private async Task ShowCoreAsync(CancellationToken cancellationToken)
    {
        Trace("ShowCoreAsync entered");
        MainWindow? owner = null;
        IntPtr ownerHwnd = IntPtr.Zero;
        IServiceScope? scope = null;
        SettingsWindow? window = null;
        OverlappedPresenter? presenter = null;
        IDisposable? webViewSuppression = null;
        TaskCompletionSource closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            owner = mainWindowAccessor.Window ??
                throw new InvalidOperationException("主窗口尚未创建。");
            ownerHwnd = WindowNative.GetWindowHandle(owner);
            if (ownerHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("主窗口 HWND 尚未创建。");
            }

            webViewSuppression = readerHost.SuppressForModal();
            scope = scopeFactory.CreateScope();
            Trace("scope created");
            window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
            Trace("SettingsWindow resolved");
            IntPtr settingsHwnd = WindowNative.GetWindowHandle(window);
            SetOwner(settingsHwnd, ownerHwnd);

            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(settingsHwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            presenter = appWindow.Presenter as OverlappedPresenter ??
                throw new InvalidOperationException("设置窗口不支持 OverlappedPresenter。");
            presenter.IsModal = true;
            _ = EnableWindow(ownerHwnd, false);
            if (IsWindowEnabled(ownerHwnd))
            {
                throw new InvalidOperationException("无法禁用设置窗口的 owner。");
            }

            Trace("owner and IsModal configured");

            window.Closed += OnClosed;
            lock (_sync)
            {
                _window = window;
                _state = SettingsWindowState.Open;
            }

            window.ViewModel.BeginOpen();
            window.Activate();
            Trace("SettingsWindow activated");
            window.ViewModel.MarkOpen();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                _ = window.DispatcherQueue.TryEnqueue(
                    () => window.RequestClose(discardChanges: true));
            });
            await closed.Task.ConfigureAwait(true);

            void OnClosed(object sender, WindowEventArgs args)
            {
                window.Closed -= OnClosed;
                closed.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            window?.Close();
            owner?.DispatcherQueue.TryEnqueue(() =>
                owner.ViewModel.Reader.StatusMessage =
                    localizer.Get("Settings_OpenWindow_Failed"));
            logger.Write(AppLogLevel.Error, "settings-window", "设置窗口打开失败。", exception);
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            if (presenter is not null)
            {
                try
                {
                    presenter.IsModal = false;
                }
                catch (Exception exception)
                {
                    logger.Write(
                        AppLogLevel.Warning,
                        "settings-window",
                        "无法清除设置窗口的原生模态标记。",
                        exception);
                }
            }

            if (ownerHwnd != IntPtr.Zero)
            {
                if (!EnableWindow(ownerHwnd, true) && !IsWindowEnabled(ownerHwnd))
                {
                    logger.Write(
                        AppLogLevel.Error,
                        "settings-window",
                        "设置窗口关闭后无法重新启用主窗口。");
                }

                _ = SetForegroundWindow(ownerHwnd);
                owner?.DispatcherQueue.TryEnqueue(owner.Activate);
            }

            webViewSuppression?.Dispose();
            lock (_sync)
            {
                _window = null;
                _state = SettingsWindowState.Closed;
            }

            window?.ViewModel.MarkClosed();
            scope?.Dispose();
        }
    }

    private static void SetOwner(IntPtr child, IntPtr owner)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetWindowLongPtr(child, GwlpHwndParent, owner);
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            throw new InvalidOperationException(
                $"无法设置设置窗口 owner HWND，Win32 错误 {error}。");
        }
    }

    private void Trace(string message)
    {
        if (!string.Equals(
                System.Environment.GetEnvironmentVariable("VDICT_WINDOW_TRACE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        logger.Write(AppLogLevel.Information, "settings-window-trace", message);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
