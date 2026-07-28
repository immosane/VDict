namespace VDict.Presentation.Services;

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel
}

public sealed record DialogRequest(
    string Title,
    string Message,
    string PrimaryText,
    string? SecondaryText = null,
    string? CancelText = null);

public interface IDialogService
{
    Task<DialogChoice> ShowAsync(
        DialogRequest request,
        CancellationToken cancellationToken = default);
}

public interface IFilePickerService
{
    Task<string?> PickDictionaryAsync(CancellationToken cancellationToken = default);

    Task<string?> PickSettingsImportAsync(CancellationToken cancellationToken = default);

    Task<string?> PickSettingsExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default);
}

public interface IModalWindowService
{
    Task ShowSettingsAsync(CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

public interface IExternalLinkService
{
    Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    Task EnqueueAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}
