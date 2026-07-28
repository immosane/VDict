using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class ReaderBrowserDiagnosticsTests
{
    [Fact]
    public void FrameProbe_CoversStyleImageFontMediaAndRequestSignals()
    {
        string probe = ReaderBrowserDiagnostics.FrameProbeScript;

        Assert.Contains("getComputedStyle", probe);
        Assert.Contains("naturalWidth", probe);
        Assert.Contains("document.fonts.check", probe);
        Assert.Contains("loadedMetadata", probe);
        Assert.Contains("performance.getEntriesByType", probe);
        Assert.Contains("requestCount", probe);
        Assert.Contains("reader.vdict.invalid", probe);
        Assert.Contains("isExternalNetworkResource", probe);
        Assert.Contains("localStoragePersisted", probe);
        Assert.Contains("legacyEvalResult", probe);
        Assert.Contains("devicePixelRatio", probe);
        Assert.Contains("elementFromPoint", probe);
        Assert.Contains("htmlPointerEvents", probe);
        Assert.Contains("bodyUserSelect", probe);
        Assert.Contains("overlays", probe);
        Assert.Contains("scrollableContainers", probe);
        Assert.Contains("htmlClientHeight", probe);
        Assert.Contains("bodyClientHeight", probe);
        Assert.Contains("documentScrollTop", probe);
        Assert.Contains("htmlOverflowY", probe);
    }

    [Fact]
    public void FrameProbe_CollectsResourceMappingsButNotDictionaryText()
    {
        string probe = ReaderBrowserDiagnostics.FrameProbeScript;

        Assert.DoesNotContain("innerText", probe);
        Assert.DoesNotContain("textContent", probe);
        Assert.Contains("originalSrc", probe);
        Assert.Contains("currentSrc", probe);
        Assert.Contains("backgroundResources", probe);
        Assert.Contains("resourceEntries", probe);
        Assert.Contains("responseStatus", probe);
    }
}
