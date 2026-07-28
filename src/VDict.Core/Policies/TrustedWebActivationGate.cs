namespace VDict.Core.Policies;

public enum TrustedWebAction
{
    ExternalLink,
    Download
}

public enum TrustedWebActivationSource
{
    WebViewUserInitiated,
    HostConfirmation
}

public sealed class TrustedWebActivationGate(TimeSpan? validityWindow = null)
{
    private readonly TimeSpan _validityWindow = validityWindow ?? TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private Ticket? _ticket;

    public void Record(
        string renderToken,
        TrustedWebAction action,
        TrustedWebActivationSource source,
        string? frameId = null,
        DateTimeOffset? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderToken);
        lock (_sync)
        {
            _ticket = new Ticket(
                renderToken,
                string.IsNullOrWhiteSpace(frameId) ? null : frameId,
                action,
                source,
                utcNow ?? DateTimeOffset.UtcNow);
        }
    }

    public bool TryConsume(
        string renderToken,
        TrustedWebAction action,
        string? frameId = null,
        DateTimeOffset? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderToken);
        lock (_sync)
        {
            Ticket? ticket = _ticket;
            _ticket = null;
            if (ticket is null ||
                (utcNow ?? DateTimeOffset.UtcNow) - ticket.RecordedAt > _validityWindow ||
                !ticket.RenderToken.Equals(renderToken, StringComparison.Ordinal) ||
                ticket.Action != action ||
                ticket.FrameId is not null &&
                !ticket.FrameId.Equals(frameId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            _ticket = null;
        }
    }

    private sealed record Ticket(
        string RenderToken,
        string? FrameId,
        TrustedWebAction Action,
        TrustedWebActivationSource Source,
        DateTimeOffset RecordedAt);
}
