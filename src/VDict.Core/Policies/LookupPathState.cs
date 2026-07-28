using System.Collections.Immutable;

namespace VDict.Core.Policies;

public enum LookupOrigin
{
    Manual,
    Candidate,
    Entry,
    SelectedText,
    History,
    Path,
    ScopeRefresh
}

public sealed record LookupPathItem(
    int Index,
    string Word,
    bool IsCurrent,
    string AccessibleName);

public static class LookupPathState
{
    public static ImmutableArray<string> Update(
        ImmutableArray<string> current,
        string destination,
        LookupOrigin origin,
        bool manualQueryEdited)
    {
        destination = destination.Trim();
        if (destination.Length == 0)
        {
            return current;
        }

        if (origin is LookupOrigin.History or LookupOrigin.Path or LookupOrigin.ScopeRefresh)
        {
            return current;
        }

        if (manualQueryEdited || current.IsDefaultOrEmpty)
        {
            return [destination];
        }

        if (origin == LookupOrigin.Candidate)
        {
            return current.SetItem(current.Length - 1, destination);
        }

        return current[^1].Equals(destination, StringComparison.OrdinalIgnoreCase)
            ? current
            : current.Add(destination);
    }

    public static ImmutableArray<LookupPathItem> CreateItems(
        ImmutableArray<string> path,
        string currentItemFormat = "Current entry: {0}",
        string pathItemFormat = "Go to path item {0}: {1}")
    {
        if (path.IsDefaultOrEmpty)
        {
            return [];
        }

        return path.Select((word, index) => new LookupPathItem(
                index,
                word,
                index == path.Length - 1,
                index == path.Length - 1
                    ? string.Format(currentItemFormat, word)
                    : string.Format(pathItemFormat, index + 1, word)))
            .ToImmutableArray();
    }
}
