using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Text;

namespace VDict.Core.Tests;

public sealed class ReaderDocumentBuilderTests
{
    [Fact]
    public void Describe_DefersEntryRewriteUntilSingleFrameBuild()
    {
        DictionaryId id = new("demo");
        LookupResult result = new(
            id,
            "Demo",
            "word",
            ["<img src=\"images/picture.png\">"]);

        ReaderDocumentDescription description =
            ReaderDocumentBuilder.Describe("word", [result], renderToken: "render");
        ReaderFrameDescriptor descriptor = Assert.Single(description.Frames);

        Assert.Equal("<img src=\"images/picture.png\">", descriptor.SourceHtml);
        ReaderFrame frame = ReaderDocumentBuilder.BuildFrame(descriptor);
        Assert.Contains(".vdict.invalid/", frame.Html);
        Assert.Contains("images/picture.png", frame.Html);
        Assert.Equal("render", description.RenderToken);
    }

    [Fact]
    public void BuildFrame_ObservesCancellationBeforeRewrite()
    {
        ReaderFrameDescriptor descriptor = new(
            "frame",
            new DictionaryId("demo"),
            new string('x', 64 * 1024));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ReaderDocumentBuilder.BuildFrame(descriptor, cancellation.Token));
    }

    [Fact]
    public void Build_RewritesRelativeResourcesAndKeepsExternalLinks()
    {
        DictionaryId id = new("demo");
        LookupResult result = new(
            id,
            "Demo",
            "word",
            [
                """
                <style>.icon { background-image: url("images/icon.png"); }</style>
                <img src="images/picture.png">
                <a href="https://example.com/page">external</a>
                """
            ]);

        ReaderDocument document = ReaderDocumentBuilder.Build("word", [result]);

        string frame = Assert.Single(document.Frames).Html;
        Assert.Contains("/resource/0/", frame);
        Assert.Contains("/images/icon.png", frame);
        Assert.Contains("/images/picture.png", frame);
        Assert.Contains("https://example.com/page", frame);
    }

    [Fact]
    public void Build_PreservesOfflineScriptFeaturesAndRewritesExternalModuleSource()
    {
        DictionaryId id = new("scripts");
        LookupResult result = new(
            id,
            "Scripts",
            "word",
            [
                """
                <button onclick="localStorage.setItem('used','1')">run</button>
                <script type="module" src="module.js"></script>
                <script type="module">import './dependency.js'; fetch('data.json')</script>
                """
            ]);

        ReaderDocument document = ReaderDocumentBuilder.Build(
            "word",
            [result],
            catalogGeneration: 3);
        string html = Assert.Single(document.Frames).Html;

        Assert.Contains("onclick=\"localStorage.setItem('used','1')\"", html);
        Assert.Contains("/resource/3/", html);
        Assert.Contains("/module.js", html);
        Assert.Contains("import './dependency.js'; fetch('data.json')", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("../../outside.png")]
    public void RewriteEntry_NeutralizesUnsafeUrls(string url)
    {
        string html = $"<img src=\"{url}\"><a href=\"{url}\">link</a>";

        string rewritten = ReaderDocumentBuilder.RewriteEntry(html, new DictionaryId("demo"));

        Assert.DoesNotContain(url, rewritten, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RewriteEntry_RewritesAudioDataSourceAndKeepsOriginalImageMapping()
    {
        DictionaryId dictionaryId = new("demo");
        const string html =
            "<span class=\"sound\" data-audio-src=\"audio/First Voice.MP3\"></span>" +
            "<img src=\"Images/Speaker Icon.PNG\">";

        string rewritten = ReaderDocumentBuilder.RewriteEntry(html, dictionaryId, 12);

        Assert.Contains("/audio/First%20Voice.MP3", rewritten, StringComparison.Ordinal);
        Assert.Contains("/Images/Speaker%20Icon.PNG", rewritten, StringComparison.Ordinal);
        Assert.Contains(
            "data-vdict-original-src=\"Images/Speaker Icon.PNG\"",
            rewritten,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteEntry_DoesNotRewriteStyleMarkupInsideScriptText()
    {
        const string html =
            "<script>const sample = '<style>.x{background:url(image.png)}</style>';</script>" +
            "<style>.real{background:url(real.png)}</style>";

        string rewritten =
            ReaderDocumentBuilder.RewriteEntry(html, new DictionaryId("demo"));

        Assert.Contains("url(image.png)", rewritten);
        Assert.Contains("/real.png", rewritten);
    }

    [Fact]
    public void Build_CarriesPerDictionarySecurityAppearanceAndCustomCss()
    {
        DictionaryId id = new("configured");
        DictionaryAppearance appearance = DictionaryAppearance.Default with
        {
            FontSize = 23
        };
        DictionaryDefinition definition = new(
            id,
            "Configured",
            null,
            @"C:\configured.mdx",
            true,
            false,
            [],
            [@"C:\configured.css"],
            appearance)
        {
            LocalScriptMode = LocalScriptMode.Legacy,
            ScriptPaths = [@"C:\dependency.js"]
        };
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Dictionaries = [definition],
            Appearance = ApplicationAppearance.Default with { FontSize = 20 },
            ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
        };
        LookupResult result = new(id, "Configured", "word", ["<p>entry</p>"]);

        ReaderDocument document = ReaderDocumentBuilder.Build("word", [result], settings);

        ReaderFrame frame = Assert.Single(document.Frames);
        Assert.False(frame.BlockNetwork);
        Assert.Equal(LocalScriptMode.Legacy, frame.LocalScriptMode);
        Assert.Equal(23, frame.Appearance.FontSize);
        Assert.Contains(
            "/resource/0/",
            Assert.Single(frame.CustomCssResourcePaths));
        Assert.EndsWith(
            "/vdict-custom-css/0.css",
            Assert.Single(frame.CustomCssResourcePaths));
        Assert.EndsWith(
            "/vdict-custom-script/0/dependency.js",
            Assert.Single(frame.CustomScriptResourcePaths));
        Assert.Equal(20, document.Appearance.FontSize);
        Assert.Equal(ExternalLinkPolicy.SystemBrowser, document.ExternalLinkPolicy);
    }

    [Fact]
    public void Build_MultipleDictionaryResults_ProducesOrderedIsolatedFrames()
    {
        LookupResult first = new(
            new DictionaryId("first"),
            "First",
            "word",
            ["<p>first-entry</p>"]);
        LookupResult second = new(
            new DictionaryId("second"),
            "Second",
            "word",
            ["<p>second-entry</p>"]);

        ReaderDocument document = ReaderDocumentBuilder.Build("word", [first, second]);
        string shell = ReaderHtmlRenderer.BuildShell(document);

        Assert.Collection(
            document.Frames,
            frame =>
            {
                Assert.Equal("frame-0000-0000", frame.FrameId);
                Assert.Equal(new DictionaryId("first"), frame.DictionaryId);
                Assert.Equal(0, frame.DictionaryResultIndex);
                Assert.Equal(0, frame.EntryIndex);
                Assert.Equal("word", frame.ResolvedWord);
                Assert.Contains("first-entry", frame.Html);
            },
            frame =>
            {
                Assert.Equal("frame-0001-0000", frame.FrameId);
                Assert.Equal(new DictionaryId("second"), frame.DictionaryId);
                Assert.Equal(1, frame.DictionaryResultIndex);
                Assert.Equal(0, frame.EntryIndex);
                Assert.Contains("second-entry", frame.Html);
            });
        Assert.Equal(2, shell.Split("<iframe class=\"entry\"", StringSplitOptions.None).Length - 1);
        Assert.True(
            shell.IndexOf("data-frame-id=\"frame-0000-0000\"", StringComparison.Ordinal) <
            shell.IndexOf("data-frame-id=\"frame-0001-0000\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MultipleEntriesFromOneDictionary_ProducesOneFramePerRecord()
    {
        DictionaryId id = new("same");
        LookupResult result = new(
            id,
            "Same",
            "resolved",
            [
                "<html><body><p>first record</p></body></html>",
                "  ",
                "<html><body><p>second record</p></body></html>"
            ]);

        ReaderDocument document = ReaderDocumentBuilder.Build("word", [result]);

        Assert.Collection(
            document.Frames,
            frame =>
            {
                Assert.Equal("frame-0000-0000", frame.FrameId);
                Assert.Equal(id, frame.DictionaryId);
                Assert.Equal(0, frame.EntryIndex);
                Assert.Contains("first record", frame.Html);
                Assert.DoesNotContain("second record", frame.Html);
            },
            frame =>
            {
                Assert.Equal("frame-0000-0002", frame.FrameId);
                Assert.Equal(id, frame.DictionaryId);
                Assert.Equal(2, frame.EntryIndex);
                Assert.Contains("second record", frame.Html);
                Assert.DoesNotContain("first record", frame.Html);
            });
    }

    [Fact]
    public void BuildWithDiagnostics_SeparatesEntryRewriteAndDocumentAssembly()
    {
        LookupResult result = new(
            new DictionaryId("timing"),
            "Timing",
            "word",
            [
                "<p><img src=\"first.png\">first</p>",
                "<p><img src=\"second.png\">second</p>"
            ]);

        ReaderDocumentBuildResult build = ReaderDocumentBuilder.BuildWithDiagnostics(
            "word",
            [result],
            catalogGeneration: 9);

        Assert.Same(build.Diagnostics, build.Document.BuildDiagnostics);
        Assert.Equal(2, build.Diagnostics.EntryCount);
        Assert.Equal(
            result.Entries.Sum(entry => entry.Length),
            build.Diagnostics.SourceCharacterCount);
        Assert.True(build.Diagnostics.RewrittenCharacterCount >
                    build.Diagnostics.SourceCharacterCount);
        Assert.True(build.Diagnostics.EntryRewriteMilliseconds >= 0);
        Assert.True(build.Diagnostics.DocumentAssemblyMilliseconds >= 0);
        Assert.True(build.Diagnostics.TotalMilliseconds >=
                    build.Diagnostics.EntryRewriteMilliseconds);
    }
}
