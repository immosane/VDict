using System.Net;

namespace VDict.Core.Text;

public enum ReaderLinkKind
{
    Entry,
    Fragment,
    Audio,
    LocalResource,
    External,
    Zoom,
    Script
}

public sealed record ReaderLink(ReaderLinkKind Kind, string Target);

public static class ReaderLinkClassifier
{
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".webm", ".wma"
        };

    private static readonly HashSet<string> ResourceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".avif", ".bmp", ".css", ".gif", ".ico", ".jpeg", ".jpg", ".js", ".otf",
            ".png", ".svg", ".ttf", ".webp", ".woff", ".woff2", ".mp4", ".mov"
        };

    public static ReaderLink Classify(string? value, string attribute = "href")
    {
        string target = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (target.StartsWith('#'))
        {
            return new(ReaderLinkKind.Fragment, target);
        }

        if (target.StartsWith("entry://", StringComparison.OrdinalIgnoreCase))
        {
            return new(ReaderLinkKind.Entry, target["entry://".Length..]);
        }

        if (target.StartsWith("sound://", StringComparison.OrdinalIgnoreCase))
        {
            return new(ReaderLinkKind.Audio, target["sound://".Length..]);
        }

        if (target.StartsWith("zoom://", StringComparison.OrdinalIgnoreCase))
        {
            return new(ReaderLinkKind.Zoom, target);
        }

        if (target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return new(ReaderLinkKind.Script, target);
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.Scheme is "http" or "https" or "mailto"
                ? new(ReaderLinkKind.External, target)
                : new(ReaderLinkKind.LocalResource, target);
        }

        string path = target.Split(['?', '#'], 2)[0];
        string extension = Path.GetExtension(path);
        if (AudioExtensions.Contains(extension))
        {
            return new(ReaderLinkKind.Audio, target);
        }

        if (!attribute.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            ResourceExtensions.Contains(extension))
        {
            return new(ReaderLinkKind.LocalResource, target);
        }

        return new(ReaderLinkKind.Entry, target);
    }
}
