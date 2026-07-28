using VDict.Presentation.Services;

namespace VDict.Presentation.Tests.TestDoubles;

internal sealed class FakeDialogService : IDialogService
{
    public DialogChoice Choice { get; set; } = DialogChoice.Cancel;

    public int ShowCount { get; private set; }

    public Task<DialogChoice> ShowAsync(
        DialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ShowCount++;
        return Task.FromResult(Choice);
    }
}
