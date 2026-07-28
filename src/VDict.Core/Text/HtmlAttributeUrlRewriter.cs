using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VDict.Core.Text;

public static partial class HtmlAttributeUrlRewriter
{
    [GeneratedRegex(
        "(?<prefix>\\b(?<name>data-audio-src|src|href|data|poster)\\s*=\\s*)(?:(?<quote>[\"'])(?<quoted>.*?)(?:\\k<quote>)|(?<plain>[^\\s\"'<>`]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UrlAttribute();

    [GeneratedRegex(
        "(?<prefix>\\bsrcset\\s*=\\s*)(?<quote>[\"'])(?<value>.*?)(?:\\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SrcSetAttribute();

    [GeneratedRegex(
        "(?<prefix>\\bstyle\\s*=\\s*)(?<quote>[\"'])(?<value>.*?)(?:\\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleAttribute();

    public static string Rewrite(
        string html,
        Func<string, string, string> rewriteUrl,
        Func<string, string> rewriteCss,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        HtmlScanResult scan = HtmlLexicalScanner.Scan(html, cancellationToken);
        StringBuilder output = new(html.Length + 64);
        int position = 0;
        int tokenIndex = 0;
        foreach (HtmlToken token in scan.Tokens)
        {
            if ((tokenIndex++ & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            (int tokenStart, int tokenLength) =
                token.SourceRange.GetOffsetAndLength(html.Length);
            if (tokenStart > position)
            {
                output.Append(html, position, tokenStart - position);
            }

            if (token.Kind != HtmlTokenKind.StartTag)
            {
                output.Append(html, tokenStart, tokenLength);
            }
            else
            {
                string tag = html.Substring(tokenStart, tokenLength);
                (int nameStart, int nameLength) =
                    token.NameRange.GetOffsetAndLength(html.Length);
                string tagName = html.Substring(nameStart, nameLength);
                output.Append(RewriteTag(tag, tagName, rewriteUrl, rewriteCss));
            }

            position = tokenStart + tokenLength;
        }

        if (position < html.Length)
        {
            output.Append(html, position, html.Length - position);
        }

        return output.ToString();
    }

    private static string RewriteTag(
        string tag,
        string tagName,
        Func<string, string, string> rewriteUrl,
        Func<string, string> rewriteCss)
    {
        string? originalImageSource = null;
        tag = UrlAttribute().Replace(tag, match =>
        {
            string name = match.Groups["name"].Value;
            string value = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value;
            string quote = match.Groups["quote"].Success ? match.Groups["quote"].Value : string.Empty;
            string rewritten = rewriteUrl(value, name);
            if (tagName.Equals("img", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("src", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals(rewritten, StringComparison.Ordinal) &&
                !rewritten.Equals("about:blank", StringComparison.OrdinalIgnoreCase) &&
                !rewritten.Equals("#", StringComparison.Ordinal))
            {
                originalImageSource = value;
            }

            return match.Groups["prefix"].Value + quote + rewritten + quote;
        });

        tag = SrcSetAttribute().Replace(tag, match =>
        {
            string rewritten = RewriteSrcSet(match.Groups["value"].Value, rewriteUrl);
            string quote = match.Groups["quote"].Value;
            return match.Groups["prefix"].Value + quote + rewritten + quote;
        });

        tag = StyleAttribute().Replace(tag, match =>
        {
            string quote = match.Groups["quote"].Value;
            return match.Groups["prefix"].Value + quote + rewriteCss(match.Groups["value"].Value) + quote;
        });

        if (originalImageSource is null ||
            tag.Contains("data-vdict-original-src", StringComparison.OrdinalIgnoreCase))
        {
            return tag;
        }

        int close = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
        return tag.Insert(
            close,
            $" data-vdict-original-src=\"{WebUtility.HtmlEncode(originalImageSource)}\"");
    }

    private static string RewriteSrcSet(
        string value,
        Func<string, string, string> rewriteUrl)
    {
        StringBuilder output = new(value.Length + 32);
        int index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ','))
            {
                output.Append(value[index++]);
            }

            if (index >= value.Length)
            {
                break;
            }

            int start = index;
            bool dataUri = value.AsSpan(index).StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            while (index < value.Length &&
                   !char.IsWhiteSpace(value[index]) &&
                   (dataUri || value[index] != ','))
            {
                index++;
            }

            output.Append(rewriteUrl(value[start..index], "src"));
            int parentheses = 0;
            while (index < value.Length)
            {
                char current = value[index++];
                output.Append(current);
                if (current == '(')
                {
                    parentheses++;
                }
                else if (current == ')' && parentheses > 0)
                {
                    parentheses--;
                }
                else if (current == ',' && parentheses == 0)
                {
                    break;
                }
            }
        }

        return output.ToString();
    }

}
