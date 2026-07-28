using System.Collections.Immutable;

namespace VDict.Core.Text;

internal enum HtmlTokenKind
{
    Text,
    StartTag,
    EndTag,
    Comment,
    Doctype,
    CData,
    ProcessingInstruction,
    RawText
}

internal readonly record struct HtmlToken(
    HtmlTokenKind Kind,
    Range SourceRange,
    Range NameRange,
    Range AttributeRange,
    bool SelfClosing)
{
    public bool NameEquals(string source, string name)
    {
        (int offset, int length) = NameRange.GetOffsetAndLength(source.Length);
        return source.AsSpan(offset, length).Equals(
            name,
            StringComparison.OrdinalIgnoreCase);
    }

    public ReadOnlySpan<char> Source(string source)
    {
        (int offset, int length) = SourceRange.GetOffsetAndLength(source.Length);
        return source.AsSpan(offset, length);
    }

    public ReadOnlySpan<char> Attributes(string source)
    {
        (int offset, int length) = AttributeRange.GetOffsetAndLength(source.Length);
        return source.AsSpan(offset, length);
    }
}

internal readonly record struct HtmlScanResult(
    ImmutableArray<HtmlToken> Tokens,
    bool IsComplete);

internal static class HtmlLexicalScanner
{
    private static readonly string[] RawTextElementNames =
        ["script", "style", "textarea", "title"];

    public static HtmlScanResult Scan(
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(source))
        {
            return new HtmlScanResult([], true);
        }

        ImmutableArray<HtmlToken>.Builder tokens = ImmutableArray.CreateBuilder<HtmlToken>();
        int position = 0;
        int nextCancellationCheck = 0;
        bool complete = true;
        while (position < source.Length)
        {
            if (position >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = position + 16 * 1024;
            }
            int tagStart = source.IndexOf('<', position);
            if (tagStart < 0)
            {
                AddText(tokens, position, source.Length);
                break;
            }

            AddText(tokens, position, tagStart);
            if (source.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = source.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(Token(HtmlTokenKind.Comment, tagStart, source.Length));
                    complete = false;
                    break;
                }

                end += 3;
                tokens.Add(Token(HtmlTokenKind.Comment, tagStart, end));
                position = end;
                continue;
            }

            if (source.AsSpan(tagStart).StartsWith("<![CDATA[", StringComparison.OrdinalIgnoreCase))
            {
                int end = source.IndexOf("]]>", tagStart + 9, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(Token(HtmlTokenKind.CData, tagStart, source.Length));
                    complete = false;
                    break;
                }

                end += 3;
                tokens.Add(Token(HtmlTokenKind.CData, tagStart, end));
                position = end;
                continue;
            }

            if (source.AsSpan(tagStart).StartsWith("<?", StringComparison.Ordinal))
            {
                int end = source.IndexOf("?>", tagStart + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(Token(
                        HtmlTokenKind.ProcessingInstruction,
                        tagStart,
                        source.Length));
                    complete = false;
                    break;
                }

                end += 2;
                tokens.Add(Token(HtmlTokenKind.ProcessingInstruction, tagStart, end));
                position = end;
                continue;
            }

            if (source.AsSpan(tagStart).StartsWith("<!", StringComparison.Ordinal))
            {
                int end = FindTagEnd(source, tagStart + 2);
                if (end < 0)
                {
                    tokens.Add(Token(HtmlTokenKind.Doctype, tagStart, source.Length));
                    complete = false;
                    break;
                }

                end++;
                tokens.Add(Token(HtmlTokenKind.Doctype, tagStart, end));
                position = end;
                continue;
            }

            if (!TryReadTag(source, tagStart, out HtmlToken tag, out int tagEnd))
            {
                // A literal '<' in dictionary text is legal enough for Chromium. Treat it
                // as text unless it starts something that resembles an unfinished tag.
                if (LooksLikeTagStart(source, tagStart))
                {
                    AddText(tokens, tagStart, source.Length);
                    complete = false;
                    break;
                }

                AddText(tokens, tagStart, tagStart + 1);
                position = tagStart + 1;
                continue;
            }

            tokens.Add(tag);
            position = tagEnd;
            if (tag.Kind != HtmlTokenKind.StartTag ||
                tag.SelfClosing ||
                !IsRawTextElement(source, tag))
            {
                continue;
            }

            int closingStart = FindClosingTag(source, position, tag);
            if (closingStart < 0)
            {
                if (position < source.Length)
                {
                    tokens.Add(Token(HtmlTokenKind.RawText, position, source.Length));
                }

                complete = false;
                break;
            }

            if (closingStart > position)
            {
                tokens.Add(Token(HtmlTokenKind.RawText, position, closingStart));
            }

            if (!TryReadTag(source, closingStart, out HtmlToken closing, out tagEnd))
            {
                AddText(tokens, closingStart, source.Length);
                complete = false;
                break;
            }

            tokens.Add(closing);
            position = tagEnd;
        }

        return new HtmlScanResult(tokens.ToImmutable(), complete);
    }

    private static bool TryReadTag(
        string source,
        int start,
        out HtmlToken token,
        out int end)
    {
        token = default;
        end = start;
        int index = start + 1;
        bool closing = index < source.Length && source[index] == '/';
        if (closing)
        {
            index++;
        }

        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        int nameStart = index;
        while (index < source.Length && IsNameCharacter(source[index]))
        {
            index++;
        }

        if (index == nameStart)
        {
            return false;
        }

        int nameEnd = index;
        int tagEnd = FindTagEnd(source, index);
        if (tagEnd < 0)
        {
            return false;
        }

        int contentEnd = tagEnd;
        while (contentEnd > nameEnd && char.IsWhiteSpace(source[contentEnd - 1]))
        {
            contentEnd--;
        }

        bool selfClosing = !closing &&
                           contentEnd > nameEnd &&
                           source[contentEnd - 1] == '/';
        int attributeEnd = selfClosing ? contentEnd - 1 : contentEnd;
        token = new HtmlToken(
            closing ? HtmlTokenKind.EndTag : HtmlTokenKind.StartTag,
            new Range(start, tagEnd + 1),
            new Range(nameStart, nameEnd),
            closing
                ? new Range(nameEnd, nameEnd)
                : new Range(nameEnd, Math.Max(nameEnd, attributeEnd)),
            selfClosing);
        end = tagEnd + 1;
        return true;
    }

    private static int FindTagEnd(string source, int start)
    {
        char quote = '\0';
        for (int index = start; index < source.Length; index++)
        {
            char current = source[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindClosingTag(string source, int start, HtmlToken opening)
    {
        (int nameOffset, int nameLength) = opening.NameRange.GetOffsetAndLength(source.Length);
        ReadOnlySpan<char> name = source.AsSpan(nameOffset, nameLength);
        int search = start;
        while (search < source.Length)
        {
            int candidate = source.IndexOf("</", search, StringComparison.Ordinal);
            if (candidate < 0)
            {
                return -1;
            }

            int candidateName = candidate + 2;
            if (candidateName + name.Length <= source.Length &&
                source.AsSpan(candidateName, name.Length).Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                int afterName = candidateName + name.Length;
                if (afterName < source.Length &&
                    (char.IsWhiteSpace(source[afterName]) || source[afterName] == '>'))
                {
                    return candidate;
                }
            }

            search = candidate + 2;
        }

        return -1;
    }

    private static bool IsRawTextElement(string source, HtmlToken token) =>
        RawTextElementNames.Any(name => token.NameEquals(source, name));

    private static bool LooksLikeTagStart(string source, int start)
    {
        int index = start + 1;
        if (index < source.Length && source[index] == '/')
        {
            index++;
        }

        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index < source.Length &&
               (char.IsLetter(source[index]) || source[index] is '!' or '?');
    }

    private static bool IsNameCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is ':' or '-' or '_';

    private static void AddText(
        ImmutableArray<HtmlToken>.Builder tokens,
        int start,
        int end)
    {
        if (end > start)
        {
            tokens.Add(Token(HtmlTokenKind.Text, start, end));
        }
    }

    private static HtmlToken Token(HtmlTokenKind kind, int start, int end) =>
        new(
            kind,
            new Range(start, end),
            new Range(start, start),
            new Range(start, start),
            false);
}
