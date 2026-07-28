using System.Net;
using VDict.Core.Models;

namespace VDict.Core.Policies;

public enum ResourceReferenceKind
{
    Local,
    Fragment,
    Entry,
    Sound,
    Data,
    Blob,
    JavaScript,
    Virtual,
    External
}

public enum ReaderResourceKind
{
    Unknown,
    Style,
    Image,
    Font,
    Audio,
    Script,
    Data
}

public enum ResourceResolutionSource
{
    DirectRequest,
    EntryHtml,
    DictionaryCss,
    CustomCss
}

public enum ResourceNamespaceKind
{
    Dictionary,
    CustomCss
}

public sealed record ResourceReferenceRequest(
    DictionaryId DictionaryId,
    string Value,
    string BasePath,
    ReaderResourceKind ResourceKind,
    ResourceResolutionSource ResolutionSource,
    ResourceNamespaceKind Namespace);

public sealed record ResourceReference(
    DictionaryId DictionaryId,
    string NormalizedPath,
    string BasePath,
    bool IsRootRelative,
    string Query,
    string Fragment,
    ReaderResourceKind ResourceKind,
    ResourceResolutionSource ResolutionSource,
    ResourceNamespaceKind Namespace,
    ResourceReferenceKind ReferenceKind,
    string OriginalValue);

public static class ResourcePathPolicy
{
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (path is null ||
            !TryResolve(
                new ResourceReferenceRequest(
                    new DictionaryId("resource"),
                    path,
                    string.Empty,
                    ReaderResourceKind.Unknown,
                    ResourceResolutionSource.DirectRequest,
                    ResourceNamespaceKind.Dictionary),
                out ResourceReference reference) ||
            reference.ReferenceKind is not (ResourceReferenceKind.Local or ResourceReferenceKind.Sound))
        {
            return false;
        }

        normalized = reference.NormalizedPath;
        return normalized.Length > 0;
    }

    public static bool TryResolve(
        ResourceReferenceRequest request,
        out ResourceReference reference)
    {
        reference = default!;
        string original = WebUtility.HtmlDecode(request.Value ?? string.Empty).Trim();
        if (original.Length == 0 || original.Contains('\0'))
        {
            return false;
        }

        if (original.StartsWith('#'))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.Fragment);
            return true;
        }

        if (original.StartsWith("entry://", StringComparison.OrdinalIgnoreCase))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.Entry);
            return true;
        }

        if (original.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.Data);
            return true;
        }

        if (original.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.Blob);
            return true;
        }

        if (original.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.JavaScript);
            return true;
        }

        if (original.StartsWith("vdict:", StringComparison.OrdinalIgnoreCase) ||
            original.StartsWith("mdd:", StringComparison.OrdinalIgnoreCase))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.Virtual);
            return true;
        }

        if (IsExternalScheme(original))
        {
            reference = NonLocal(request, original, ResourceReferenceKind.External);
            return true;
        }

        bool sound = original.StartsWith("sound://", StringComparison.OrdinalIgnoreCase);
        string localValue = sound ? original["sound://".Length..] : original;
        SplitSuffix(localValue, out string resourcePart, out string query, out string fragment);

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(resourcePart).Replace('\\', '/').Trim();
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (decoded.Length == 0 ||
            decoded.Contains('\0') ||
            decoded.Any(character => character < ' ') ||
            decoded.StartsWith("//", StringComparison.Ordinal) ||
            IsDrivePath(decoded))
        {
            return false;
        }

        bool rootRelative = decoded.StartsWith('/');
        string localPath = rootRelative ? decoded.TrimStart('/') : decoded;
        string normalizedBase = string.Empty;
        string combined = localPath;
        if (!rootRelative && !string.IsNullOrWhiteSpace(request.BasePath))
        {
            string baseValue = request.BasePath.Replace('\\', '/').TrimStart('/');
            if (!TryNormalizeSegments(baseValue, out normalizedBase))
            {
                return false;
            }

            int slash = normalizedBase.LastIndexOf('/');
            string directory = slash < 0 ? string.Empty : normalizedBase[..slash];
            combined = directory.Length == 0
                ? localPath
                : directory + "/" + localPath;
        }

        if (!TryNormalizeSegments(combined, out string normalizedPath))
        {
            return false;
        }

        ReaderResourceKind kind = request.ResourceKind == ReaderResourceKind.Unknown
            ? InferKind(normalizedPath)
            : request.ResourceKind;
        reference = new ResourceReference(
            request.DictionaryId,
            normalizedPath,
            normalizedBase,
            rootRelative,
            query,
            fragment,
            kind,
            request.ResolutionSource,
            request.Namespace,
            sound ? ResourceReferenceKind.Sound : ResourceReferenceKind.Local,
            original);
        return true;
    }

    public static string BuildLegacyResourceUrl(ResourceReference reference)
    {
        if (reference.ReferenceKind is not (ResourceReferenceKind.Local or ResourceReferenceKind.Sound))
        {
            return reference.OriginalValue;
        }

        string escapedPath = string.Join(
            "/",
            reference.NormalizedPath.Split('/').Select(Uri.EscapeDataString));
        string suffix = reference.Query.Length == 0 ? string.Empty : "?" + reference.Query;
        if (reference.Fragment.Length > 0)
        {
            suffix += "#" + reference.Fragment;
        }

        return $"vdict://resource/{Uri.EscapeDataString(reference.DictionaryId.Value)}/" +
               escapedPath + suffix;
    }

    private static ResourceReference NonLocal(
        ResourceReferenceRequest request,
        string original,
        ResourceReferenceKind kind) =>
        new(
            request.DictionaryId,
            string.Empty,
            request.BasePath,
            false,
            string.Empty,
            string.Empty,
            request.ResourceKind,
            request.ResolutionSource,
            request.Namespace,
            kind,
            original);

    private static bool TryNormalizeSegments(string value, out string normalized)
    {
        normalized = string.Empty;
        Stack<string> segments = new();
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }

                segments.Pop();
                continue;
            }

            if (segment.Contains(':') || segment.Contains('\0'))
            {
                return false;
            }

            segments.Push(segment);
        }

        normalized = string.Join('/', segments.Reverse());
        return normalized.Length > 0;
    }

    private static void SplitSuffix(
        string value,
        out string resource,
        out string query,
        out string fragment)
    {
        fragment = string.Empty;
        int fragmentIndex = value.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            fragment = value[(fragmentIndex + 1)..];
            value = value[..fragmentIndex];
        }

        query = string.Empty;
        int queryIndex = value.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = value[(queryIndex + 1)..];
            value = value[..queryIndex];
        }

        resource = value;
    }

    private static bool IsExternalScheme(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static bool IsDrivePath(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static ReaderResourceKind InferKind(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            return ReaderResourceKind.Style;
        }

        if (extension is ".ttf" or ".otf" or ".woff" or ".woff2")
        {
            return ReaderResourceKind.Font;
        }

        if (extension is ".mp3" or ".wav" or ".ogg" or ".aac" or ".m4a" or
            ".flac" or ".opus" or ".webm" or ".wma")
        {
            return ReaderResourceKind.Audio;
        }

        if (extension is ".js" or ".mjs")
        {
            return ReaderResourceKind.Script;
        }

        if (extension is ".json")
        {
            return ReaderResourceKind.Data;
        }

        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or
            ".webp" or ".avif" or ".bmp" or ".ico"
            ? ReaderResourceKind.Image
            : ReaderResourceKind.Unknown;
    }
}
