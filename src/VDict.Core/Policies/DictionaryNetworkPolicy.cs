using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VDict.Core.Policies;

public static class DictionaryNetworkPolicy
{
    // Synthetic documents use a registered WebView2 scheme so their isolated
    // hosts never enter the machine's DNS/proxy path.
    public const string InternalScheme = "vdict-doc";
    public const string InternalFrameHostSuffix = ".vdict.invalid";
    public const string ShellHost = "reader.vdict.invalid";
    public const string ShellOrigin = InternalScheme + "://reader.vdict.invalid";
    public const string WelcomeLogoUri = ShellOrigin + "/assets/logo_rect.png";
    public static readonly bool LegacyVdictFallbackEnabled = true;
    private static readonly byte[] CapabilitySecret = RandomNumberGenerator.GetBytes(32);

    public static string FrameHostForDictionary(string dictionaryId)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dictionaryId)))[..24]
            .ToLowerInvariant();
        return $"d-{hash}{InternalFrameHostSuffix}";
    }

    public static string FrameUriForDictionary(
        string dictionaryId,
        string renderToken,
        string frameId) =>
        $"{InternalScheme}://{FrameHostForDictionary(dictionaryId)}/render/" +
        $"{Uri.EscapeDataString(renderToken)}/{Uri.EscapeDataString(frameId)}.html";

    public static string ShellUri(string renderToken) =>
        $"{ShellOrigin}/render/{Uri.EscapeDataString(renderToken)}/shell.html";

    public static bool IsWelcomeLogoUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals(ShellHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/assets/logo_rect.png", StringComparison.Ordinal) &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0;

    public static string FrameOriginForDictionary(string dictionaryId) =>
        $"{InternalScheme}://{FrameHostForDictionary(dictionaryId)}";

    public static string ResourceCapabilityForDictionary(
        string dictionaryId,
        long catalogGeneration)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            catalogGeneration.ToString(CultureInfo.InvariantCulture) + "\n" + dictionaryId);
        return Convert.ToHexString(HMACSHA256.HashData(CapabilitySecret, payload))[..40]
            .ToLowerInvariant();
    }

    public static string ResourceUriForDictionary(
        string dictionaryId,
        long catalogGeneration,
        string resourceCapability,
        string normalizedPath,
        string query = "",
        string fragment = "")
    {
        string escaped = string.Join(
            "/",
            normalizedPath.Split('/').Select(Uri.EscapeDataString));
        string suffix = query.Length == 0 ? string.Empty : "?" + query;
        if (fragment.Length > 0)
        {
            suffix += "#" + fragment;
        }

        return $"{FrameOriginForDictionary(dictionaryId)}/resource/" +
               $"{catalogGeneration.ToString(CultureInfo.InvariantCulture)}/" +
               $"{Uri.EscapeDataString(resourceCapability)}/{escaped}{suffix}";
    }

    public static string ResourceBaseUriForDictionary(
        string dictionaryId,
        long catalogGeneration,
        string resourceCapability) =>
        $"{FrameOriginForDictionary(dictionaryId)}/resource/" +
        $"{catalogGeneration.ToString(CultureInfo.InvariantCulture)}/" +
        $"{Uri.EscapeDataString(resourceCapability)}/";

    public static bool IsAuthorizedResourceRequest(
        string dictionaryId,
        long expectedGeneration,
        string expectedCapability,
        string requestHost,
        long requestGeneration,
        string requestCapability)
    {
        if (!FrameHostForDictionary(dictionaryId)
                .Equals(requestHost, StringComparison.OrdinalIgnoreCase) ||
            expectedGeneration != requestGeneration)
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(expectedCapability);
        byte[] actual = Encoding.UTF8.GetBytes(requestCapability);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static bool IsAuthorizedResourceRequest(
        string dictionaryId,
        string? origin,
        string? referer)
    {
        string expected = FrameOriginForDictionary(dictionaryId);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return origin.TrimEnd('/').Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(referer, UriKind.Absolute, out Uri? source) &&
               source.GetLeftPart(UriPartial.Authority)
                   .Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetFrameDictionary(
        string? origin,
        string? referer,
        IEnumerable<Models.ReaderFrame> frames,
        out Models.ReaderFrame? frame)
    {
        frame = null;
        string? source = !string.IsNullOrWhiteSpace(origin)
            ? origin
            : Uri.TryCreate(referer, UriKind.Absolute, out Uri? refererUri)
                ? refererUri.GetLeftPart(UriPartial.Authority)
                : null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        frame = frames.FirstOrDefault(candidate =>
            FrameOriginForDictionary(candidate.DictionaryId.Value)
                .Equals(source.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return frame is not null;
    }

    public static bool TryParseShellUri(string? value, out string renderToken)
    {
        renderToken = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(ShellHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 3 ||
            !segments[0].Equals("render", StringComparison.Ordinal) ||
            !segments[2].Equals("shell.html", StringComparison.Ordinal))
        {
            return false;
        }

        renderToken = Uri.UnescapeDataString(segments[1]);
        return renderToken.Length > 0;
    }

    public static bool TryParseFrameUri(
        string? value,
        out string host,
        out string renderToken,
        out string frameId)
    {
        host = string.Empty;
        renderToken = string.Empty;
        frameId = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) ||
            !IsDictionaryHost(uri.Host))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 3 ||
            !segments[0].Equals("render", StringComparison.Ordinal) ||
            !segments[2].EndsWith(".html", StringComparison.Ordinal))
        {
            return false;
        }

        host = uri.Host;
        renderToken = Uri.UnescapeDataString(segments[1]);
        frameId = Uri.UnescapeDataString(segments[2][..^5]);
        return renderToken.Length > 0 && frameId.Length > 0;
    }

    public static bool TryParseResourceUri(
        string? value,
        out string host,
        out long catalogGeneration,
        out string resourceCapability,
        out string resourcePath)
    {
        host = string.Empty;
        catalogGeneration = 0;
        resourceCapability = string.Empty;
        resourcePath = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) ||
            !IsDictionaryHost(uri.Host))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/', 4);
        if (segments.Length != 4 ||
            !segments[0].Equals("resource", StringComparison.Ordinal) ||
            !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out catalogGeneration) ||
            catalogGeneration < 0)
        {
            return false;
        }

        host = uri.Host;
        resourceCapability = Uri.UnescapeDataString(segments[2]);
        resourcePath = Uri.UnescapeDataString(segments[3]);
        return resourceCapability.Length is > 0 and <= 128 && resourcePath.Length > 0;
    }

    public static bool TryParseLegacyResourceUri(
        string? value,
        out string dictionaryId,
        out string resourcePath)
    {
        dictionaryId = string.Empty;
        resourcePath = string.Empty;
        if (!LegacyVdictFallbackEnabled ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("vdict", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("resource", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/', 2);
        if (segments.Length != 2)
        {
            return false;
        }

        dictionaryId = Uri.UnescapeDataString(segments[0]);
        resourcePath = Uri.UnescapeDataString(segments[1]);
        return dictionaryId.Length > 0 && resourcePath.Length > 0;
    }

    public static bool ShouldBlock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return true;
        }

        return !IsAllowedUri(uri);
    }

    public static bool IsAllowedUri(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) && IsAllowedUri(uri);

    public static bool IsAllowedUri(Uri uri) =>
        (LegacyVdictFallbackEnabled &&
         uri.Scheme.Equals("vdict", StringComparison.OrdinalIgnoreCase) &&
         uri.Host.Equals("resource", StringComparison.OrdinalIgnoreCase)) ||
        (uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) &&
         (uri.Host.Equals(ShellHost, StringComparison.OrdinalIgnoreCase) ||
          IsDictionaryHost(uri.Host))) ||
        uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase);

    public static bool IsInternalDocumentUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme.Equals(InternalScheme, StringComparison.OrdinalIgnoreCase) &&
        (uri.Host.Equals(ShellHost, StringComparison.OrdinalIgnoreCase) ||
         IsDictionaryHost(uri.Host));

    public static bool IsInternalHttpsUri(string? value) =>
        IsInternalDocumentUri(value);

    private static bool IsDictionaryHost(string host) =>
        host.StartsWith("d-", StringComparison.OrdinalIgnoreCase) &&
        host.EndsWith(InternalFrameHostSuffix, StringComparison.OrdinalIgnoreCase) &&
        !host.Equals(ShellHost, StringComparison.OrdinalIgnoreCase);
}
