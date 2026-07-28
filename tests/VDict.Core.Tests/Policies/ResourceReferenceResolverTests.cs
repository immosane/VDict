using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class ResourceReferenceResolverTests
{
    private static readonly DictionaryId DictionaryId = new("dict");

    [Theory]
    [InlineData("/font/a.ttf", "styles/main.css", "font/a.ttf", true)]
    [InlineData("\\font\\a.ttf", "styles/main.css", "font/a.ttf", true)]
    [InlineData("font/a.ttf", "styles/main.css", "styles/font/a.ttf", false)]
    [InlineData("./font/a.ttf", "styles/main.css", "styles/font/a.ttf", false)]
    [InlineData("../font/a.ttf", "styles/main.css", "font/a.ttf", false)]
    [InlineData("img/a.png", "", "img/a.png", false)]
    [InlineData("/img/a.png", "", "img/a.png", true)]
    public void LocalPathResolution_PreservesVisibleResourceSemantics(
        string value,
        string basePath,
        string expected,
        bool rootRelative)
    {
        Assert.True(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                value,
                basePath,
                ReaderResourceKind.Unknown,
                ResourceResolutionSource.DictionaryCss,
                ResourceNamespaceKind.Dictionary),
            out ResourceReference reference));

        Assert.Equal(expected, reference.NormalizedPath);
        Assert.Equal(rootRelative, reference.IsRootRelative);
    }

    [Fact]
    public void QueryAndFragment_AreExcludedFromLookupAndPreservedInUrl()
    {
        Assert.True(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                "font/A.woff2?v=2#face",
                "styles/main.css",
                ReaderResourceKind.Font,
                ResourceResolutionSource.DictionaryCss,
                ResourceNamespaceKind.Dictionary),
            out ResourceReference reference));

        Assert.Equal("styles/font/A.woff2", reference.NormalizedPath);
        Assert.Equal("v=2", reference.Query);
        Assert.Equal("face", reference.Fragment);
        Assert.EndsWith(
            "/styles/font/A.woff2?v=2#face",
            ResourcePathPolicy.BuildLegacyResourceUrl(reference));
    }

    [Theory]
    [InlineData("#part", ResourceReferenceKind.Fragment)]
    [InlineData("entry://word", ResourceReferenceKind.Entry)]
    [InlineData("sound://voice/a.mp3", ResourceReferenceKind.Sound)]
    [InlineData("data:image/png;base64,AA==", ResourceReferenceKind.Data)]
    [InlineData("blob:https://example.test/id", ResourceReferenceKind.Blob)]
    [InlineData("javascript:alert(1)", ResourceReferenceKind.JavaScript)]
    [InlineData("https://example.test/a.png", ResourceReferenceKind.External)]
    public void Classification_PrecedesLocalPathNormalization(
        string value,
        ResourceReferenceKind expected)
    {
        Assert.True(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                value,
                "styles/main.css",
                ReaderResourceKind.Unknown,
                ResourceResolutionSource.DictionaryCss,
                ResourceNamespaceKind.Dictionary),
            out ResourceReference reference));

        Assert.Equal(expected, reference.ReferenceKind);
    }

    [Theory]
    [InlineData("../../outside.png")]
    [InlineData("%2e%2e/%2e%2e/outside.png")]
    [InlineData("C:/Windows/a.png")]
    [InlineData("C:\\Windows\\a.png")]
    [InlineData("//server/share/a.png")]
    [InlineData("\\\\server\\share\\a.png")]
    [InlineData("file.txt:stream")]
    [InlineData("a%00b.png")]
    public void PhysicalOrEscapingPaths_AreRejected(string value)
    {
        Assert.False(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                value,
                "styles/main.css",
                ReaderResourceKind.Unknown,
                ResourceResolutionSource.DictionaryCss,
                ResourceNamespaceKind.Dictionary),
            out _));
    }

    [Fact]
    public void DecodeAndEncode_HappenExactlyOnceAndPreserveUnicodeCaseAndSpaces()
    {
        Assert.True(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                "%E5%AD%97%E4%BD%93/My%20Font%2520A.TTF",
                "",
                ReaderResourceKind.Font,
                ResourceResolutionSource.EntryHtml,
                ResourceNamespaceKind.Dictionary),
            out ResourceReference reference));

        Assert.Equal("字体/My Font%20A.TTF", reference.NormalizedPath);
        Assert.EndsWith(
            "/%E5%AD%97%E4%BD%93/My%20Font%2520A.TTF",
            ResourcePathPolicy.BuildLegacyResourceUrl(reference));
    }

    [Fact]
    public void CustomCssNamespace_DoesNotChangeDictionaryRoot()
    {
        Assert.True(ResourcePathPolicy.TryResolve(
            new ResourceReferenceRequest(
                DictionaryId,
                "../icons/a.svg",
                "themes/sub/main.css",
                ReaderResourceKind.Image,
                ResourceResolutionSource.CustomCss,
                ResourceNamespaceKind.CustomCss),
            out ResourceReference reference));

        Assert.Equal(ResourceNamespaceKind.CustomCss, reference.Namespace);
        Assert.Equal("themes/icons/a.svg", reference.NormalizedPath);
    }
}
