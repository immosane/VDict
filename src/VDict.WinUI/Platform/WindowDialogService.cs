using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VDict.Presentation.Services;

namespace VDict.WinUI.Platform;

public sealed class WindowDialogService : IDialogService
{
    private FrameworkElement? _root;

    public void Attach(FrameworkElement root) => _root = root;

    public async Task<DialogChoice> ShowAsync(
        DialogRequest request,
        CancellationToken cancellationToken = default)
    {
        FrameworkElement root = _root ??
            throw new InvalidOperationException("对话框尚未绑定窗口 XamlRoot。");
        ContentDialog dialog = new()
        {
            XamlRoot = root.XamlRoot,
            Title = request.Title,
            Content = request.Message,
            PrimaryButtonText = request.PrimaryText,
            SecondaryButtonText = request.SecondaryText ?? string.Empty,
            CloseButtonText = request.CancelText ?? string.Empty,
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult result = await dialog.ShowAsync().AsTask(cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            ContentDialogResult.Primary => DialogChoice.Primary,
            ContentDialogResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel
        };
    }
}
