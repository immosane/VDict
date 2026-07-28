using VDict.Presentation.Services;
using Windows.ApplicationModel.DataTransfer;

namespace VDict.WinUI.Platform;

public sealed class ClipboardService : IClipboardService
{
    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }
}
