using VDict.Core.Models;

namespace VDict.Core.Policies;

public static class CandidateRequestGuard
{
    public static bool IsCurrent(
        long requestedVersion,
        long currentVersion,
        string requestedText,
        string currentText,
        DictionaryId? requestedDictionaryId,
        DictionaryId? currentDictionaryId,
        long resultCatalogGeneration,
        long currentCatalogGeneration)
    {
        return requestedVersion == currentVersion &&
               requestedDictionaryId == currentDictionaryId &&
               resultCatalogGeneration == currentCatalogGeneration &&
               requestedText.Equals(currentText, StringComparison.OrdinalIgnoreCase);
    }
}
