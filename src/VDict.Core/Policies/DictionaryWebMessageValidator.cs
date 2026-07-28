using System.Text.Json;
using VDict.Core.Models;

namespace VDict.Core.Policies;

public static class DictionaryWebMessageValidator
{
    private static readonly HashSet<string> AllowedKinds =
    [
        "entry",
        "selection",
        "userActivation",
        "inputEvent",
        "imageError",
        "mediaEvent",
        "mediaError",
        "mediaMetadata",
        "mediaWarmup",
        "zoomDelta",
        "zoomStep",
        "zoomReset",
        "linkDebug",
        "frameReady",
        "frameLoad",
        "browserDiagnostic"
    ];

    public static bool TryValidateEnvelope(
        JsonElement root,
        string expectedRenderToken,
        IEnumerable<ReaderFrame> frames,
        out string kind)
    {
        kind = string.Empty;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetBoundedString(root, "kind", 80, out kind) ||
            !TryGetBoundedString(root, "renderToken", 64, out string renderToken) ||
            !TryGetBoundedString(root, "frameId", 256, out string frameId) ||
            !TryGetBoundedString(root, "dictionaryId", 128, out string dictionaryId) ||
            !AllowedKinds.Contains(kind) ||
            !renderToken.Equals(expectedRenderToken, StringComparison.Ordinal))
        {
            return false;
        }

        return frames.Any(frame =>
            frame.FrameId.Equals(frameId, StringComparison.Ordinal) &&
            frame.DictionaryId.Value.Equals(dictionaryId, StringComparison.Ordinal));
    }

    public static bool TryGetBoundedString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string candidate = element.GetString() ?? string.Empty;
        if (candidate.Length > maximumLength || candidate.Contains('\0'))
        {
            return false;
        }

        value = candidate;
        return true;
    }
}
