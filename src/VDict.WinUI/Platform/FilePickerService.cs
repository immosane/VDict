using Microsoft.UI.Xaml;
using VDict.Core.Localization;
using VDict.Presentation.Services;
using VDict.WinUI.Views;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VDict.WinUI.Platform;

public sealed class PickerWindowContext(MainWindowAccessor mainWindow)
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    public Window GetWindow() => _window ?? mainWindow.Window ??
        throw new InvalidOperationException("文件选择器没有可用的 owner 窗口。");
}

public sealed class FilePickerService(
    PickerWindowContext windows,
    IAppLocalizer localizer) : IFilePickerService
{
    public Task<string?> PickDictionaryAsync(CancellationToken cancellationToken = default) =>
        PickOpenAsync([".mdx"], cancellationToken);

    public Task<string?> PickSettingsImportAsync(CancellationToken cancellationToken = default) =>
        PickOpenAsync([".json"], cancellationToken);

    public async Task<string?> PickSettingsExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        Window window = windows.GetWindow();
        FileSavePicker picker = new()
        {
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add(localizer.Get("FilePicker_SettingsJson"), [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file =
            await picker.PickSaveFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return file?.Path;
    }

    private async Task<string?> PickOpenAsync(
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken)
    {
        Window window = windows.GetWindow();
        FileOpenPicker picker = new();
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        Windows.Storage.StorageFile? file =
            await picker.PickSingleFileAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return file?.Path;
    }
}
