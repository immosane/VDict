using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class DictionaryHtmlNormalizerTests
{
    [Fact]
    public void FullDocument_ExtractsEnvelopeAndBodyAttributes()
    {
        const string html =
            "<!doctype html><html lang=\"en\"><head><style>.x{color:red}</style></head>" +
            "<body class=\"entry\"><p>content</p></body></html>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.FullDocument, result.Diagnostics.Kind);
        Assert.Contains(".x{color:red}", result.HeadContent);
        Assert.Equal("<p>content</p>", result.BodyContent);
        Assert.Equal(" class=\"entry\"", result.BodyAttributes);
        Assert.Equal(0, result.Diagnostics.DroppedPayloadLength);
    }

    [Fact]
    public void BodyDocument_ExtractsOnlyWhenNothingVisibleSurroundsBody()
    {
        const string html =
            "<head><link rel=\"stylesheet\" href=\"a.css\"></head>\n" +
            "<body data-entry=\"1\"><p>content</p></body><!-- trailing -->";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.BodyDocument, result.Diagnostics.Kind);
        Assert.Contains("a.css", result.HeadContent);
        Assert.Equal("<p>content</p>", result.BodyContent);
        Assert.Equal(" data-entry=\"1\"", result.BodyAttributes);
    }

    [Fact]
    public void Composite_PreservesContentBeforeBetweenAndAfterMultipleBodies()
    {
        const string html =
            "<head><style>.x{color:red}</style></head>" +
            "<p id=\"before\">A</p>" +
            "<body eid=\"one\"><p>B</p></body>" +
            "<i id=\"middle\">C</i>" +
            "<body eid=\"two\"><p>D</p></body>" +
            "<p id=\"after\">E</p>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.Composite, result.Diagnostics.Kind);
        Assert.Contains("id=\"before\">A", result.BodyContent);
        Assert.Contains("<p>B</p>", result.BodyContent);
        Assert.Contains("id=\"middle\">C", result.BodyContent);
        Assert.Contains("<p>D</p>", result.BodyContent);
        Assert.Contains("id=\"after\">E", result.BodyContent);
        Assert.Equal(2, result.Diagnostics.BodyElementCount);
        Assert.Equal(0, result.Diagnostics.DroppedPayloadLength);
    }

    [Fact]
    public void SingleBodyWithVisibleSiblings_IsCompositeAndNotTruncated()
    {
        const string html =
            "<p>before</p><body><p>inside</p></body><p>after</p>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.Composite, result.Diagnostics.Kind);
        Assert.Contains("before", result.BodyContent);
        Assert.Contains("inside", result.BodyContent);
        Assert.Contains("after", result.BodyContent);
    }

    [Fact]
    public void FullEnvelopeWithVisibleContentOutsideBody_IsComposite()
    {
        const string html =
            "<html><head><title>x</title></head><p>before</p>" +
            "<body><p>inside</p></body><p>after</p></html>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.Composite, result.Diagnostics.Kind);
        Assert.Contains("before", result.BodyContent);
        Assert.Contains("inside", result.BodyContent);
        Assert.Contains("after", result.BodyContent);
    }

    [Fact]
    public void SimilarNamesAndRawText_DoNotCountAsBodyElements()
    {
        const string html =
            "<body-g></body-g><body2></body2><x:body></x:body>" +
            "<script>const value = '<body>x</body>';</script>" +
            "<div data-name=\"body\">content</div>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.Fragment, result.Diagnostics.Kind);
        Assert.Equal(0, result.Diagnostics.BodyElementCount);
        Assert.Equal(html, result.BodyContent);
    }

    [Fact]
    public void Ambiguous_RemovesSecurityNodesButPreservesPayload()
    {
        const string html =
            "<base href=\"https://evil.test/\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src *\">" +
            "<script nonce=\"attacker\">payload()</script><div";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.Ambiguous, result.Diagnostics.Kind);
        Assert.DoesNotContain("<base", result.BodyContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Security-Policy", result.BodyContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nonce=", result.BodyContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payload()", result.BodyContent);
        Assert.Contains("<div", result.BodyContent);
        Assert.Equal(0, result.Diagnostics.DroppedPayloadLength);
    }

    [Fact]
    public void SecurityCleanup_DoesNotAlterScriptStrings()
    {
        const string html =
            "<body nonce=\"attacker\"><script>" +
            "const sample = '<base nonce=\"text\">';" +
            "</script><p nonce='attacker'>payload</p></body>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Equal(DictionaryHtmlKind.BodyDocument, result.Diagnostics.Kind);
        Assert.DoesNotContain("nonce=", result.BodyAttributes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<p nonce=", result.BodyContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<base nonce=\"text\">", result.BodyContent);
    }

    [Fact]
    public void StylesRelocateExceptInsideProtectedContainers()
    {
        const string html =
            "<style>.top{color:red}</style>" +
            "<link rel=\"alternate stylesheet\" href=\"a.css\">" +
            "<template><style>.template{color:blue}</style></template>" +
            "<svg><style>.svg{fill:red}</style></svg><p>x</p>";

        DictionaryHtmlNormalizationResult result =
            DictionaryHtmlNormalizer.Normalize(html);

        Assert.Contains(".top{color:red}", result.HeadContent);
        Assert.Contains("a.css", result.HeadContent);
        Assert.Contains(".template{color:blue}", result.BodyContent);
        Assert.Contains(".svg{fill:red}", result.BodyContent);
        Assert.DoesNotContain(".top{color:red}", result.BodyContent);
    }
}
