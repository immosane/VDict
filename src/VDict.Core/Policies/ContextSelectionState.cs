namespace VDict.Core.Policies;

public sealed class ContextSelectionState(int maximumLength = 256)
{
    private readonly int _maximumLength = Math.Max(1, maximumLength);

    public string Value { get; private set; } = string.Empty;

    public bool BeginMenu(bool hasSelection, string? selectionText)
    {
        Value = string.Empty;
        if (!hasSelection || string.IsNullOrWhiteSpace(selectionText))
        {
            return false;
        }

        string normalized = selectionText.Trim();
        Value = normalized[..Math.Min(normalized.Length, _maximumLength)];
        return true;
    }

    public void Clear() => Value = string.Empty;
}
