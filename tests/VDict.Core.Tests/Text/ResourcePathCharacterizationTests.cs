using VDict.Core.Models;
using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class ResourcePathCharacterizationTests
{
    [Theory]
    [InlineData("/font/a.ttf", "/font/a.ttf")]
    [InlineData("\\font\\a.ttf", "/font/a.ttf")]
    [InlineData("img/a.png", "/img/a.png")]
    public void EntryResources_AreRootedAtDictionaryNamespace(
        string input,
        string expected)
    {
        string html = ReaderDocumentBuilder.RewriteEntry(
            $"<img src=\"{input}\">",
            new DictionaryId("dict"));

        Assert.Contains(expected, html);
    }

    [Fact]
    public void EntryResourceEscape_IsRejected()
    {
        string html = ReaderDocumentBuilder.RewriteEntry(
            "<img src=\"../outside.png\">",
            new DictionaryId("dict"));

        Assert.Contains("about:blank", html);
    }
}
