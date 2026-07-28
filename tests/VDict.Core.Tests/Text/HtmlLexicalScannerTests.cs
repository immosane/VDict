using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class HtmlLexicalScannerTests
{
    [Fact]
    public void Scan_RecognizesExactNamesAndQuotedGreaterThan()
    {
        const string html =
            "<BODY data-value='a>b'><body-g><body2><x:body></BODY>";

        HtmlScanResult result = HtmlLexicalScanner.Scan(html);

        Assert.True(result.IsComplete);
        Assert.Collection(
            result.Tokens,
            token =>
            {
                Assert.Equal(HtmlTokenKind.StartTag, token.Kind);
                Assert.True(token.NameEquals(html, "body"));
                Assert.Equal(" data-value='a>b'", token.Attributes(html).ToString());
            },
            token => Assert.True(token.NameEquals(html, "body-g")),
            token => Assert.True(token.NameEquals(html, "body2")),
            token => Assert.True(token.NameEquals(html, "x:body")),
            token =>
            {
                Assert.Equal(HtmlTokenKind.EndTag, token.Kind);
                Assert.True(token.NameEquals(html, "body"));
            });
    }

    [Fact]
    public void Scan_DoesNotTokenizeMarkupInsideCommentsOrRawText()
    {
        const string html =
            "<!-- <body> --><script>const x = '<body>';</script>" +
            "<style>.x::after{content:'<body>'}</style>" +
            "<textarea><body></textarea><title><body></title>";

        HtmlScanResult result = HtmlLexicalScanner.Scan(html);

        Assert.True(result.IsComplete);
        Assert.Equal(4, result.Tokens.Count(token =>
            token.Kind == HtmlTokenKind.StartTag));
        Assert.DoesNotContain(
            result.Tokens,
            token => token.Kind == HtmlTokenKind.StartTag &&
                     token.NameEquals(html, "body"));
        Assert.Equal(4, result.Tokens.Count(token =>
            token.Kind == HtmlTokenKind.RawText));
    }

    [Theory]
    [InlineData("<div")]
    [InlineData("<!-- incomplete")]
    [InlineData("<script>incomplete")]
    [InlineData("<![CDATA[incomplete")]
    [InlineData("<?incomplete")]
    public void Scan_MalformedInputIsIncompleteWithoutThrowing(string html)
    {
        HtmlScanResult result = HtmlLexicalScanner.Scan(html);

        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.Tokens);
    }

    [Fact]
    public void Scan_HandlesLargeInputLinearly()
    {
        string html = "<div data-value=\">\">" +
                      new string('x', 1_000_000) +
                      "</div>";

        HtmlScanResult result = HtmlLexicalScanner.Scan(html);

        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Tokens.Length);
    }
}
