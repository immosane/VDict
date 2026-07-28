namespace VDict.Core.Policies;

public enum CandidateSelectionOrigin
{
    None,
    Keyboard,
    Pointer
}

public sealed class CandidateSelectionState
{
    public string? ExplicitWord { get; private set; }

    public CandidateSelectionOrigin Origin { get; private set; }

    public void Reset()
    {
        ExplicitWord = null;
        Origin = CandidateSelectionOrigin.None;
    }

    public void Mark(string? word, CandidateSelectionOrigin origin)
    {
        string? normalized = word?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || origin == CandidateSelectionOrigin.None)
        {
            Reset();
            return;
        }

        ExplicitWord = normalized;
        Origin = origin;
    }

    public string ResolveEnterQuery(string input) =>
        string.IsNullOrWhiteSpace(ExplicitWord) ? input.Trim() : ExplicitWord;

    public static int MoveIndex(int currentIndex, int itemCount, int direction)
    {
        if (itemCount <= 0 || direction == 0)
        {
            return -1;
        }

        if (currentIndex < 0 || currentIndex >= itemCount)
        {
            return direction > 0 ? 0 : itemCount - 1;
        }

        return direction > 0
            ? currentIndex >= itemCount - 1 ? 0 : currentIndex + 1
            : currentIndex <= 0 ? itemCount - 1 : currentIndex - 1;
    }
}
