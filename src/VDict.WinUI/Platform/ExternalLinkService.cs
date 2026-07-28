using VDict.Presentation.Services;
using Windows.System;

namespace VDict.WinUI.Platform;

public sealed class ExternalLinkService : IExternalLinkService
{
    public async Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return await Launcher.LaunchUriAsync(uri).AsTask(cancellationToken).ConfigureAwait(false);
    }
}
