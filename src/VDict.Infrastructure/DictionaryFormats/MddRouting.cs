namespace VDict.Infrastructure.DictionaryFormats;

internal static class MddRouting
{
    public static int Priority(
        string mddPath,
        DictionaryResourceKind resourceKind,
        MddResourceProfile? profile)
    {
        int heuristic = HeuristicPriority(mddPath, resourceKind);
        if (profile is null || profile.TotalCount == 0)
        {
            return 100 + heuristic;
        }

        int matching = profile.Count(resourceKind);
        if (matching <= 0)
        {
            return 1000 + heuristic;
        }

        int concentrationPenalty =
            100 - (int)Math.Round(100d * matching / profile.TotalCount);
        return concentrationPenalty + heuristic / 10;
    }

    private static int HeuristicPriority(
        string path,
        DictionaryResourceKind kind)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        bool isOne = stem.EndsWith(".1", StringComparison.OrdinalIgnoreCase);
        bool isTwo = stem.EndsWith(".2", StringComparison.OrdinalIgnoreCase);
        bool isMain = !isOne &&
                      !isTwo &&
                      !stem.EndsWith(".3", StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            DictionaryResourceKind.Image =>
                isTwo ? 0 : isMain ? 10 : isOne ? 100 : 30,
            DictionaryResourceKind.Audio =>
                isOne ? 0 : isMain ? 10 : isTwo ? 100 : 30,
            DictionaryResourceKind.Style or
                DictionaryResourceKind.Font or
                DictionaryResourceKind.Script =>
                isMain ? 0 : 30,
            _ => isMain ? 0 : isOne ? 20 : isTwo ? 30 : 10
        };
    }
}
