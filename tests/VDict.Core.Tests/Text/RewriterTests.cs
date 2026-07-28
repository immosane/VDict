using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class RewriterTests
{
    [Fact]
    public void HtmlRewriter_RewritesDictionaryAudioDataSource()
    {
        const string html = "<span class=\"sound\" data-audio-src=\"audio/First Voice.MP3\">play</span>";

        string result = HtmlAttributeUrlRewriter.Rewrite(
            html,
            (value, attribute) => $"/safe/{attribute}/{value}",
            css => css);

        Assert.Contains(
            "data-audio-src=\"/safe/data-audio-src/audio/First Voice.MP3\"",
            result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CssRewriter_PreservesCommentsAndStrings()
    {
        const string css = """/* url(no.png) */ .x{background:url("a.png");content:'url(text.png)'}""";

        string result = CssUrlRewriter.Rewrite(css, value => $"vdict://resource/d/{value}");

        Assert.Contains("/* url(no.png) */", result, StringComparison.Ordinal);
        Assert.Contains("""url("vdict://resource/d/a.png")""", result, StringComparison.Ordinal);
        Assert.Contains("""content:'url(text.png)'""", result, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlRewriter_RewritesAttributesButNotScriptBodiesOrComments()
    {
        const string html =
            """<!-- <img src="comment.png"> --><img src="a.png" style="background:url(b.png)"><script>const x='<img src="script.png">';</script>""";

        string result = HtmlAttributeUrlRewriter.Rewrite(
            html,
            (value, _) => $"/safe/{value}",
            css => CssUrlRewriter.Rewrite(css, value => $"/safe/{value}"));

        Assert.Contains("""<!-- <img src="comment.png"> -->""", result, StringComparison.Ordinal);
        Assert.Contains("src=\"/safe/a.png\"", result, StringComparison.Ordinal);
        Assert.Contains("url(/safe/b.png)", result, StringComparison.Ordinal);
        Assert.Contains("src=\"script.png\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void CssRewriter_RewritesUrlAndQuotedImageSetCandidatesWithoutTouchingType()
    {
        const string css =
            "@font-face{src:url(font.woff2?v=2#face)}" +
            ".x{background-image:image-set(\"one a.png\" 1x,url(two.png) 2x,type(\"image/png\"))}";

        string result = CssUrlRewriter.Rewrite(css, value => $"/safe/{value}");

        Assert.Contains("url(/safe/font.woff2?v=2#face)", result);
        Assert.Contains("image-set(\"/safe/one a.png\" 1x,url(/safe/two.png) 2x,type(\"image/png\"))", result);
    }

    [Fact]
    public void HtmlRewriter_PreservesDataUriCommaWhenRewritingSrcSet()
    {
        const string html =
            "<img srcset=\"data:image/svg+xml,%3Csvg%3E 1x, image%20two.png 2x\">";

        string result = HtmlAttributeUrlRewriter.Rewrite(
            html,
            (value, _) => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? value
                : $"/safe/{value}",
            css => css);

        Assert.Contains("data:image/svg+xml,%3Csvg%3E 1x", result);
        Assert.Contains("/safe/image%20two.png 2x", result);
    }

    [Fact]
    public void CssRewriter_PreservesModernCascadeAndAnimationSyntax()
    {
        const string css =
            "@supports(display:grid){@container card (width>20rem){.x{" +
            "--gap:clamp(1rem,2vw,2rem);display:grid;gap:calc(var(--gap)/2)}}}" +
            "@media(prefers-reduced-motion:no-preference){@keyframes fade{to{opacity:1}}}";

        Assert.Equal(css, CssUrlRewriter.Rewrite(css, value => "/safe/" + value));
    }
}
