using System.Collections.Immutable;

namespace VDict.Core.Policies;

public sealed class LookupHistory(int capacity)
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly List<LookupHistoryEntry> _back = [];
    private readonly List<LookupHistoryEntry> _forward = [];

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public void Record(string currentWord, IReadOnlyList<string> currentPath, string destinationWord)
    {
        if (string.IsNullOrWhiteSpace(currentWord) ||
            currentWord.Equals(destinationWord, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Add(_back, currentWord, currentPath);
        _forward.Clear();
    }

    public void RecordSnapshot(string currentWord, IReadOnlyList<string> currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentWord))
        {
            return;
        }

        Add(_back, currentWord, currentPath);
        _forward.Clear();
    }

    public bool TryGoBack(
        string currentWord,
        IReadOnlyList<string> currentPath,
        out LookupHistoryEntry destination)
    {
        if (!TryPeekBack(out destination))
        {
            return false;
        }

        return CommitBack(currentWord, currentPath, destination);
    }

    public bool TryGoForward(
        string currentWord,
        IReadOnlyList<string> currentPath,
        out LookupHistoryEntry destination)
    {
        if (!TryPeekForward(out destination))
        {
            return false;
        }

        return CommitForward(currentWord, currentPath, destination);
    }

    public bool TryPeekBack(out LookupHistoryEntry destination) =>
        TryPeek(_back, out destination);

    public bool TryPeekForward(out LookupHistoryEntry destination) =>
        TryPeek(_forward, out destination);

    public bool CommitBack(
        string currentWord,
        IReadOnlyList<string> currentPath,
        LookupHistoryEntry expectedDestination)
    {
        if (!MatchesTop(_back, expectedDestination))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(currentWord))
        {
            Add(_forward, currentWord, currentPath);
        }

        _ = Pop(_back);
        return true;
    }

    public bool CommitForward(
        string currentWord,
        IReadOnlyList<string> currentPath,
        LookupHistoryEntry expectedDestination)
    {
        if (!MatchesTop(_forward, expectedDestination))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(currentWord))
        {
            Add(_back, currentWord, currentPath);
        }

        _ = Pop(_forward);
        return true;
    }

    private static bool TryPeek(
        List<LookupHistoryEntry> source,
        out LookupHistoryEntry destination)
    {
        if (source.Count == 0)
        {
            destination = default!;
            return false;
        }

        destination = source[^1];
        return true;
    }

    private static bool MatchesTop(
        List<LookupHistoryEntry> source,
        LookupHistoryEntry expectedDestination) =>
        source.Count > 0 && ReferenceEquals(source[^1], expectedDestination);

    private void Add(List<LookupHistoryEntry> target, string word, IReadOnlyList<string> path)
    {
        target.Add(new LookupHistoryEntry(word, [.. path]));
        if (target.Count > _capacity)
        {
            target.RemoveRange(0, target.Count - _capacity);
        }
    }

    private static LookupHistoryEntry Pop(List<LookupHistoryEntry> source)
    {
        LookupHistoryEntry entry = source[^1];
        source.RemoveAt(source.Count - 1);
        return entry;
    }
}

public sealed record LookupHistoryEntry(string Word, ImmutableArray<string> Path);
