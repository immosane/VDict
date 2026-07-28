using System.Text.Json;
using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class SecurityPolicyTests
{
    [Theory]
    [InlineData("vdict://resource/dict/image.png", false)]
    [InlineData("data:image/png;base64,AA==", false)]
    [InlineData("https://example.test/a", true)]
    [InlineData("file:///c:/secret.txt", true)]
    [InlineData("javascript:alert(1)", true)]
    public void NetworkPolicy_BlocksUntrustedSchemes(string uri, bool expected)
    {
        Assert.Equal(expected, DictionaryNetworkPolicy.ShouldBlock(uri));
    }

    [Fact]
    public void FrameUri_RoundTripsThroughIsolatedDictionaryHost()
    {
        string uri = DictionaryNetworkPolicy.FrameUriForDictionary(
            "Oxford 中文",
            "render-token",
            "frame-0");

        Assert.True(DictionaryNetworkPolicy.TryParseFrameUri(
            uri,
            out string host,
            out string renderToken,
            out string frameId));
        Assert.Equal(
            DictionaryNetworkPolicy.FrameHostForDictionary("Oxford 中文"),
            host);
        Assert.Equal("render-token", renderToken);
        Assert.Equal("frame-0", frameId);
        Assert.False(DictionaryNetworkPolicy.TryParseFrameUri(
            "https://example.test/render/render-token/frame-0",
            out _,
            out _,
            out _));
    }

    [Fact]
    public void WebMessage_RequiresMatchingTokenAndFrameIdentity()
    {
        using JsonDocument json = JsonDocument.Parse(
            """{"kind":"selection","renderToken":"token","frameId":"frame","dictionaryId":"dict"}""");
        ReaderFrame[] frames = [new("frame", new DictionaryId("dict"), "<p/>")];

        Assert.True(DictionaryWebMessageValidator.TryValidateEnvelope(
            json.RootElement,
            "token",
            frames,
            out string kind));
        Assert.Equal("selection", kind);
        Assert.False(DictionaryWebMessageValidator.TryValidateEnvelope(
            json.RootElement,
            "other",
            frames,
            out _));
    }

    [Fact]
    public void WebMessage_AllowsActivationTelemetryButRejectsExternalLinkCommands()
    {
        ReaderFrame[] frames = [new("frame", new DictionaryId("dict"), "<p/>")];
        using JsonDocument activation = JsonDocument.Parse(
            """{"kind":"userActivation","renderToken":"token","frameId":"frame","dictionaryId":"dict","source":"mouse"}""");
        using JsonDocument external = JsonDocument.Parse(
            """{"kind":"externalLink","renderToken":"token","frameId":"frame","dictionaryId":"dict","href":"https://example.test"}""");

        Assert.True(DictionaryWebMessageValidator.TryValidateEnvelope(
            activation.RootElement,
            "token",
            frames,
            out _));
        Assert.False(DictionaryWebMessageValidator.TryValidateEnvelope(
            external.RootElement,
            "token",
            frames,
            out _));
    }

    [Fact]
    public void ShellAndDictionaryOrigins_AreStableAndStrictlySeparated()
    {
        Assert.Equal("vdict-doc://reader.vdict.invalid", DictionaryNetworkPolicy.ShellOrigin);
        Assert.NotEqual(
            DictionaryNetworkPolicy.ShellOrigin,
            DictionaryNetworkPolicy.FrameOriginForDictionary("dict"));
        Assert.StartsWith("d-", DictionaryNetworkPolicy.FrameHostForDictionary("dict"));
    }

    [Fact]
    public void WelcomeLogo_UsesOnlyTheFixedShellAssetEndpoint()
    {
        Assert.True(DictionaryNetworkPolicy.IsWelcomeLogoUri(
            DictionaryNetworkPolicy.WelcomeLogoUri));
        Assert.False(DictionaryNetworkPolicy.IsWelcomeLogoUri(
            DictionaryNetworkPolicy.WelcomeLogoUri + "?version=2"));
        Assert.False(DictionaryNetworkPolicy.IsWelcomeLogoUri(
            "vdict-doc://reader.vdict.invalid/assets/other.png"));
        Assert.False(DictionaryNetworkPolicy.IsWelcomeLogoUri(
            "https://reader.vdict.invalid/assets/logo_rect.png"));
    }

    [Fact]
    public void ResourceEndpoint_BindsHostGenerationCapabilityAndNormalizedPath()
    {
        string capability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary("dict", 7);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            7,
            capability,
            "字体/My Font.woff2",
            "v=2",
            "face");

        Assert.True(DictionaryNetworkPolicy.TryParseResourceUri(
            uri,
            out string host,
            out long generation,
            out string parsedCapability,
            out string path));
        Assert.Equal(DictionaryNetworkPolicy.FrameHostForDictionary("dict"), host);
        Assert.Equal(7, generation);
        Assert.Equal(capability, parsedCapability);
        Assert.Equal("字体/My Font.woff2", path);
        Assert.EndsWith("?v=2#face", uri);
        Assert.Equal(
            capability,
            DictionaryNetworkPolicy.ResourceCapabilityForDictionary("dict", 7));
        Assert.NotEqual(
            capability,
            DictionaryNetworkPolicy.ResourceCapabilityForDictionary("other", 7));
        Assert.NotEqual(
            capability,
            DictionaryNetworkPolicy.ResourceCapabilityForDictionary("dict", 8));
    }

    [Theory]
    [InlineData("http://example.test/a")]
    [InlineData("https://example.test/a")]
    [InlineData("ws://example.test/a")]
    [InlineData("wss://example.test/a")]
    [InlineData("ftp://example.test/a")]
    public void RealNetwork_IsAlwaysBlockedRegardlessOfLegacyFlag(string uri)
    {
        Assert.True(DictionaryNetworkPolicy.ShouldBlock(uri));
    }

    [Fact]
    public void InternalDocumentDetection_DoesNotTrustLookalikeOrRealNetworkHosts()
    {
        Assert.True(DictionaryNetworkPolicy.IsInternalDocumentUri(
            DictionaryNetworkPolicy.ShellUri("token")));
        Assert.True(DictionaryNetworkPolicy.IsInternalDocumentUri(
            DictionaryNetworkPolicy.FrameUriForDictionary("dict", "token", "frame")));
        Assert.False(DictionaryNetworkPolicy.IsInternalDocumentUri(
            "vdict-doc://reader.vdict.invalid.example.test/render/token/shell.html"));
        Assert.False(DictionaryNetworkPolicy.IsInternalDocumentUri(
            "https://reader.vdict.invalid/render/token/shell.html"));
        Assert.False(DictionaryNetworkPolicy.IsInternalDocumentUri("https://example.test/a"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("selection\0forged")]
    public void WebMessage_RejectsUnknownOrMalformedKinds(string kind)
    {
        string payload = JsonSerializer.Serialize(new
        {
            kind,
            renderToken = "token",
            frameId = "frame",
            dictionaryId = "dict"
        });
        using JsonDocument json = JsonDocument.Parse(payload);
        ReaderFrame[] frames = [new("frame", new DictionaryId("dict"), "<p/>")];

        Assert.False(DictionaryWebMessageValidator.TryValidateEnvelope(
            json.RootElement,
            "token",
            frames,
            out _));
    }

    [Theory]
    [InlineData("mediaError")]
    [InlineData("mediaMetadata")]
    [InlineData("mediaWarmup")]
    public void WebMessage_AllowsBoundMediaDiagnostics(string kind)
    {
        string payload = JsonSerializer.Serialize(new
        {
            kind,
            renderToken = "token",
            frameId = "frame",
            dictionaryId = "dict"
        });
        using JsonDocument json = JsonDocument.Parse(payload);

        Assert.True(DictionaryWebMessageValidator.TryValidateEnvelope(
            json.RootElement,
            "token",
            [new ReaderFrame("frame", new DictionaryId("dict"), "")],
            out _));
    }

    [Theory]
    [InlineData("images/../icons/a.png", true, "icons/a.png")]
    [InlineData("../secret.txt", false, "")]
    [InlineData("file:///c:/secret.txt", false, "")]
    [InlineData("sound://audio/a.mp3", true, "audio/a.mp3")]
    [InlineData("a%00b", false, "")]
    public void ResourcePath_NormalizesWithoutAllowingEscape(
        string input,
        bool expected,
        string expectedPath)
    {
        Assert.Equal(expected, ResourcePathPolicy.TryNormalize(input, out string path));
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("..\\evil.exe", "evil.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("", "download")]
    public void DownloadName_IsReducedToSafeLeaf(string input, string expected)
    {
        Assert.Equal(expected, DownloadFileNamePolicy.Sanitize(input));
    }

    [Theory]
    [InlineData("bytes=0-9", 100, true, 0, 9)]
    [InlineData("bytes=95-", 100, true, 95, 99)]
    [InlineData("items=0-9", 100, false, 0, 99)]
    public void ByteRange_ParsesSingleRange(
        string value,
        int length,
        bool expected,
        int start,
        int end)
    {
        Assert.Equal(expected, HttpByteRangeParser.TryParse(value, length, out int actualStart, out int actualEnd));
        Assert.Equal(start, actualStart);
        Assert.Equal(end, actualEnd);
    }

    [Theory]
    [InlineData("bytes=-10", 100, true, 90, 99)]
    [InlineData("bytes=100-110", 100, false, 0, 99)]
    [InlineData("bytes=9-2", 100, false, 0, 99)]
    [InlineData("bytes=0-1,4-5", 100, false, 0, 99)]
    public void ByteRange_RejectsInvalidAndSupportsSuffix(
        string value,
        int length,
        bool expected,
        int start,
        int end)
    {
        Assert.Equal(expected, HttpByteRangeParser.TryParse(value, length, out int actualStart, out int actualEnd));
        Assert.Equal(start, actualStart);
        Assert.Equal(end, actualEnd);
    }

    [Fact]
    public void ResourceRequest_RequiresMatchingDictionaryFrameOrigin()
    {
        string expected = DictionaryNetworkPolicy.FrameOriginForDictionary("dict-a");
        string other = DictionaryNetworkPolicy.FrameOriginForDictionary("dict-b");

        Assert.True(DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
            "dict-a",
            expected,
            null));
        Assert.True(DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
            "dict-a",
            null,
            expected + "/render/token/frame"));
        Assert.False(DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
            "dict-a",
            other,
            other + "/render/token/frame"));
        Assert.False(DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
            "dict-a",
            null,
            null));
    }

    [Fact]
    public void MimeDiagnostics_ReportsExtensionSignatureMismatch()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        string? diagnostic = ResourceMimeDiagnostics.DescribeMismatch(
            "wrong.jpg",
            "image/jpeg",
            png);

        Assert.Contains("image/png", diagnostic);
        Assert.Null(ResourceMimeDiagnostics.DescribeMismatch(
            "right.png",
            "image/png",
            png));
    }
}
