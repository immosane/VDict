using System.Text;

namespace VDict.Core.Text;

public static class CssUrlRewriter
{
    public static string Rewrite(
        string css,
        Func<string, string> rewriteUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(css))
        {
            return css;
        }

        StringBuilder output = new(css.Length + 32);
        int nextCancellationCheck = 0;
        for (int index = 0; index < css.Length;)
        {
            if (index >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = index + 16 * 1024;
            }
            if (index + 1 < css.Length && css[index] == '/' && css[index + 1] == '*')
            {
                int end = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
                int length = end < 0 ? css.Length - index : end + 2 - index;
                output.Append(css, index, length);
                index += length;
                continue;
            }

            if (css[index] is '\'' or '"')
            {
                index = CopyString(css, index, output);
                continue;
            }

            if (TryGetImageSetFunction(css, index, out int imageSetOpenParen))
            {
                int closeParen = FindMatchingParen(css, imageSetOpenParen);
                if (closeParen < 0)
                {
                    output.Append(css, index, css.Length - index);
                    break;
                }

                output.Append(css, index, imageSetOpenParen + 1 - index);
                output.Append(RewriteImageSetCandidates(
                    css[(imageSetOpenParen + 1)..closeParen],
                    rewriteUrl,
                    cancellationToken));
                output.Append(')');
                index = closeParen + 1;
                continue;
            }

            if (!IsUrlFunction(css, index, out int openParen))
            {
                output.Append(css[index++]);
                continue;
            }

            output.Append(css, index, openParen + 1 - index);
            index = openParen + 1;
            while (index < css.Length && char.IsWhiteSpace(css[index]))
            {
                output.Append(css[index++]);
            }

            if (index < css.Length && css[index] is '\'' or '"')
            {
                char quote = css[index++];
                output.Append(quote);
                string value = ReadStringValue(css, ref index, quote);
                output.Append(rewriteUrl(DecodeCssEscapes(value)));
                if (index < css.Length && css[index] == quote)
                {
                    output.Append(css[index++]);
                }
            }
            else
            {
                int start = index;
                while (index < css.Length && css[index] != ')')
                {
                    index += css[index] == '\\' && index + 1 < css.Length ? 2 : 1;
                }

                output.Append(rewriteUrl(DecodeCssEscapes(css[start..index].Trim())));
            }

            while (index < css.Length && char.IsWhiteSpace(css[index]))
            {
                output.Append(css[index++]);
            }

            if (index < css.Length && css[index] == ')')
            {
                output.Append(css[index++]);
            }
        }

        return output.ToString();
    }

    private static string RewriteImageSetCandidates(
        string content,
        Func<string, string> rewriteUrl,
        CancellationToken cancellationToken)
    {
        string urlsRewritten = Rewrite(content, rewriteUrl, cancellationToken);
        StringBuilder output = new(urlsRewritten.Length + 16);
        bool candidateStart = true;
        int depth = 0;
        for (int index = 0; index < urlsRewritten.Length;)
        {
            char current = urlsRewritten[index];
            if (current is '\'' or '"')
            {
                char quote = current;
                output.Append(quote);
                index++;
                string value = ReadStringValue(urlsRewritten, ref index, quote);
                output.Append(candidateStart && depth == 0
                    ? rewriteUrl(DecodeCssEscapes(value))
                    : value);
                if (index < urlsRewritten.Length && urlsRewritten[index] == quote)
                {
                    output.Append(urlsRewritten[index++]);
                }

                candidateStart = false;
                continue;
            }

            output.Append(current);
            index++;
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                candidateStart = true;
            }
            else if (!char.IsWhiteSpace(current) && depth == 0)
            {
                candidateStart = false;
            }
        }

        return output.ToString();
    }

    private static bool TryGetImageSetFunction(string css, int index, out int openParen)
    {
        openParen = -1;
        ReadOnlySpan<string> names = ["image-set", "-webkit-image-set"];
        foreach (string name in names)
        {
            if (index + name.Length > css.Length ||
                !css.AsSpan(index, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                index > 0 && (char.IsLetterOrDigit(css[index - 1]) || css[index - 1] is '-' or '_'))
            {
                continue;
            }

            int cursor = index + name.Length;
            while (cursor < css.Length && char.IsWhiteSpace(css[cursor]))
            {
                cursor++;
            }

            if (cursor < css.Length && css[cursor] == '(')
            {
                openParen = cursor;
                return true;
            }
        }

        return false;
    }

    private static int FindMatchingParen(string css, int openParen)
    {
        int depth = 0;
        char quote = '\0';
        for (int index = openParen; index < css.Length; index++)
        {
            char current = css[index];
            if (quote != '\0')
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsUrlFunction(string css, int index, out int openParen)
    {
        openParen = -1;
        if (index + 3 > css.Length ||
            !css.AsSpan(index, 3).Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index > 0 && (char.IsLetterOrDigit(css[index - 1]) || css[index - 1] is '-' or '_'))
        {
            return false;
        }

        int cursor = index + 3;
        while (cursor < css.Length && char.IsWhiteSpace(css[cursor]))
        {
            cursor++;
        }

        if (cursor >= css.Length || css[cursor] != '(')
        {
            return false;
        }

        openParen = cursor;
        return true;
    }

    private static int CopyString(string css, int index, StringBuilder output)
    {
        char quote = css[index];
        output.Append(css[index++]);
        while (index < css.Length)
        {
            char current = css[index++];
            output.Append(current);
            if (current == '\\' && index < css.Length)
            {
                output.Append(css[index++]);
            }
            else if (current == quote)
            {
                break;
            }
        }

        return index;
    }

    private static string ReadStringValue(string css, ref int index, char quote)
    {
        StringBuilder value = new();
        while (index < css.Length && css[index] != quote)
        {
            if (css[index] == '\\' && index + 1 < css.Length)
            {
                value.Append(css[index++]);
                value.Append(css[index++]);
            }
            else
            {
                value.Append(css[index++]);
            }
        }

        return value.ToString();
    }

    private static string DecodeCssEscapes(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        StringBuilder decoded = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                decoded.Append(current);
                continue;
            }

            index++;
            if (value[index] is '\r' or '\n' or '\f')
            {
                if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            int start = index;
            int digits = 0;
            while (index < value.Length && digits < 6 && Uri.IsHexDigit(value[index]))
            {
                index++;
                digits++;
            }

            if (digits == 0)
            {
                decoded.Append(value[index]);
                continue;
            }

            int codePoint = Convert.ToInt32(value.Substring(start, digits), 16);
            decoded.Append(codePoint is > 0 and <= 0x10ffff
                ? char.ConvertFromUtf32(codePoint)
                : "\ufffd");
            if (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                // CSS consumes one whitespace character after a hexadecimal escape.
            }
            else
            {
                index--;
            }
        }

        return decoded.ToString();
    }
}
