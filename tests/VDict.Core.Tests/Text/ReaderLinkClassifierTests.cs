using VDict.Core.Models;
using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class ReaderLinkClassifierTests
{
    [Theory]
    [InlineData("entry://hello", ReaderLinkKind.Entry)]
    [InlineData("hello world", ReaderLinkKind.Entry)]
    [InlineData("#usage", ReaderLinkKind.Fragment)]
    [InlineData("sound://voice/a.mp3", ReaderLinkKind.Audio)]
    [InlineData("voice/a.ogg", ReaderLinkKind.Audio)]
    [InlineData("images/a.png", ReaderLinkKind.LocalResource)]
    [InlineData("https://example.test", ReaderLinkKind.External)]
    [InlineData("zoom://image", ReaderLinkKind.Zoom)]
    [InlineData("javascript:void(0)", ReaderLinkKind.Script)]
    public void Href_IsClassifiedIntoStrongKind(string href, ReaderLinkKind expected)
    {
        ReaderLink link = ReaderLinkClassifier.Classify(href, "href");

        Assert.Equal(expected, link.Kind);
    }

    [Fact]
    public void OrdinaryHref_IsRewrittenAsEntryNavigation()
    {
        string html = ReaderDocumentBuilder.RewriteEntry(
            """<a href="hello world">go</a>""",
            new DictionaryId("demo"));

        Assert.Contains("entry://hello%20world", html);
    }
}
