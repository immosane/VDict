using System.Collections.Immutable;
using System.Text;

namespace VDict.Core.Text;

internal enum DictionaryHtmlKind
{
    FullDocument,
    BodyDocument,
    Fragment,
    Composite,
    Ambiguous
}

[Flags]
internal enum DictionaryHtmlWarning
{
    None = 0,
    MultipleHtmlElements = 1,
    MultipleBodyElements = 2,
    VisibleContentOutsideBody = 4,
    UnbalancedTag = 8,
    ConflictingBodyAttributes = 16,
    ConservativeFallback = 32
}

internal sealed record DictionaryHtmlNormalizationDiagnostics(
    DictionaryHtmlKind Kind,
    int SourceLength,
    int HtmlElementCount,
    int HeadElementCount,
    int BodyElementCount,
    int RemovedWrapperLength,
    int RemovedSecurityLength,
    int RelocatedStyleLength,
    int DroppedPayloadLength,
    DictionaryHtmlWarning Warnings);

internal sealed record DictionaryHtmlNormalizationResult(
    string HeadContent,
    string BodyContent,
    string BodyAttributes,
    DictionaryHtmlNormalizationDiagnostics Diagnostics);

internal static class DictionaryHtmlNormalizer
{
    private readonly record struct ElementBounds(
        int OpenStart,
        int OpenEnd,
        int CloseStart,
        int CloseEnd)
    {
        public int InnerStart => OpenEnd;

        public int InnerLength => CloseStart - OpenEnd;

        public int OuterLength => CloseEnd - OpenStart;
    }

    private readonly record struct SanitizedText(string Value, int RemovedLength);

    private readonly record struct RelocatedText(
        string Body,
        string Head,
        int RelocatedLength);

    public static DictionaryHtmlNormalizationResult Normalize(
        string source,
        CancellationToken cancellationToken = default)
    {
        source ??= string.Empty;
        HtmlScanResult scan = HtmlLexicalScanner.Scan(source, cancellationToken);
        ImmutableArray<HtmlToken> tokens = scan.Tokens;
        int htmlCount = CountStartTags(tokens, source, "html");
        int headCount = CountStartTags(tokens, source, "head");
        int bodyCount = CountStartTags(tokens, source, "body");
        DictionaryHtmlWarning warnings = DictionaryHtmlWarning.None;
        if (htmlCount > 1)
        {
            warnings |= DictionaryHtmlWarning.MultipleHtmlElements;
        }

        if (bodyCount > 1)
        {
            warnings |= DictionaryHtmlWarning.MultipleBodyElements;
        }

        DictionaryHtmlKind kind;
        ElementBounds? html = FindUniqueElement(tokens, source, "html");
        ElementBounds? head = FindUniqueElement(tokens, source, "head");
        ElementBounds? body = FindUniqueElement(tokens, source, "body");
        bool unbalancedDocumentTag =
            HasUnbalancedDocumentElement(tokens, source, "html") ||
            HasUnbalancedDocumentElement(tokens, source, "head") ||
            HasUnbalancedDocumentElement(tokens, source, "body");
        if (!scan.IsComplete || unbalancedDocumentTag)
        {
            kind = DictionaryHtmlKind.Ambiguous;
            warnings |= DictionaryHtmlWarning.UnbalancedTag |
                        DictionaryHtmlWarning.ConservativeFallback;
        }
        else if (htmlCount > 1 || bodyCount > 1)
        {
            kind = DictionaryHtmlKind.Composite;
            warnings |= DictionaryHtmlWarning.ConservativeFallback;
        }
        else if (htmlCount == 1)
        {
            ImmutableArray<Range> ignoredHead = head is not null
                ? [new Range(head.Value.OpenStart, head.Value.CloseEnd)]
                : [];
            bool validEnvelope =
                html is not null &&
                body is not null &&
                body.Value.OpenStart >= html.Value.OpenEnd &&
                body.Value.CloseEnd <= html.Value.CloseStart &&
                !HasVisibleContentOutside(
                    source,
                    tokens,
                    0,
                    html.Value.OpenStart,
                    []) &&
                !HasVisibleContentOutside(
                    source,
                    tokens,
                    html.Value.CloseEnd,
                    source.Length,
                    []) &&
                !HasVisibleContentOutside(
                    source,
                    tokens,
                    html.Value.OpenEnd,
                    body.Value.OpenStart,
                    ignoredHead) &&
                !HasVisibleContentOutside(
                    source,
                    tokens,
                    body.Value.CloseEnd,
                    html.Value.CloseStart,
                    []);
            if (validEnvelope)
            {
                kind = DictionaryHtmlKind.FullDocument;
            }
            else
            {
                kind = DictionaryHtmlKind.Composite;
                warnings |= DictionaryHtmlWarning.VisibleContentOutsideBody |
                            DictionaryHtmlWarning.ConservativeFallback;
            }
        }
        else if (bodyCount == 1 && body is not null)
        {
            ImmutableArray<Range> ignoredBeforeBody = head is not null &&
                                                      head.Value.CloseEnd <= body.Value.OpenStart
                ? [new Range(head.Value.OpenStart, head.Value.CloseEnd)]
                : [];
            bool visibleBefore = HasVisibleContentOutside(
                source,
                tokens,
                0,
                body.Value.OpenStart,
                ignoredBeforeBody);
            bool visibleAfter = HasVisibleContentOutside(
                source,
                tokens,
                body.Value.CloseEnd,
                source.Length,
                []);
            if (visibleBefore || visibleAfter)
            {
                kind = DictionaryHtmlKind.Composite;
                warnings |= DictionaryHtmlWarning.VisibleContentOutsideBody |
                            DictionaryHtmlWarning.ConservativeFallback;
            }
            else
            {
                kind = DictionaryHtmlKind.BodyDocument;
            }
        }
        else
        {
            kind = DictionaryHtmlKind.Fragment;
        }

        string headContent = string.Empty;
        string bodyContent;
        string bodyAttributes = string.Empty;
        int removedWrapperLength = 0;
        if (kind == DictionaryHtmlKind.FullDocument &&
            html is not null &&
            body is not null)
        {
            if (head is not null &&
                head.Value.OpenStart >= html.Value.OpenEnd &&
                head.Value.CloseEnd <= body.Value.OpenStart)
            {
                headContent = source.Substring(
                    head.Value.InnerStart,
                    head.Value.InnerLength);
                removedWrapperLength +=
                    head.Value.OuterLength - head.Value.InnerLength;
            }

            bodyContent = source.Substring(
                body.Value.InnerStart,
                body.Value.InnerLength);
            bodyAttributes = AttributeText(source, tokens, body.Value.OpenStart);
            removedWrapperLength +=
                html.Value.OuterLength -
                html.Value.InnerLength +
                body.Value.OuterLength -
                body.Value.InnerLength;
        }
        else if (kind == DictionaryHtmlKind.BodyDocument && body is not null)
        {
            if (head is not null && head.Value.CloseEnd <= body.Value.OpenStart)
            {
                headContent = source.Substring(
                    head.Value.InnerStart,
                    head.Value.InnerLength);
                removedWrapperLength +=
                    head.Value.OuterLength - head.Value.InnerLength;
            }

            bodyContent = source.Substring(
                body.Value.InnerStart,
                body.Value.InnerLength);
            bodyAttributes = AttributeText(source, tokens, body.Value.OpenStart);
            removedWrapperLength +=
                body.Value.OuterLength - body.Value.InnerLength;
        }
        else if (kind == DictionaryHtmlKind.Fragment &&
                 head is not null &&
                 IsLeadingMetadataElement(source, tokens, head.Value))
        {
            headContent = source.Substring(
                head.Value.InnerStart,
                head.Value.InnerLength);
            bodyContent = source.Remove(head.Value.OpenStart, head.Value.OuterLength);
            removedWrapperLength +=
                head.Value.OuterLength - head.Value.InnerLength;
        }
        else
        {
            bodyContent = source;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SanitizedText safeHead = Sanitize(headContent, cancellationToken);
        SanitizedText safeBody = Sanitize(bodyContent, cancellationToken);
        SanitizedText safeAttributes = SanitizeAttributeList(bodyAttributes);
        RelocatedText relocated = RelocateStyles(safeBody.Value, cancellationToken);
        if (relocated.Head.Length > 0)
        {
            safeHead = safeHead with
            {
                Value = safeHead.Value.Length == 0
                    ? relocated.Head
                    : safeHead.Value + System.Environment.NewLine + relocated.Head
            };
        }

        int removedSecurityLength =
            safeHead.RemovedLength +
            safeBody.RemovedLength +
            safeAttributes.RemovedLength;
        return new DictionaryHtmlNormalizationResult(
            safeHead.Value,
            relocated.Body,
            safeAttributes.Value,
            new DictionaryHtmlNormalizationDiagnostics(
                kind,
                source.Length,
                htmlCount,
                headCount,
                bodyCount,
                removedWrapperLength,
                removedSecurityLength,
                relocated.RelocatedLength,
                0,
                warnings));
    }

    private static int CountStartTags(
        ImmutableArray<HtmlToken> tokens,
        string source,
        string name) =>
        tokens.Count(token =>
            token.Kind == HtmlTokenKind.StartTag &&
            token.NameEquals(source, name));

    private static ElementBounds? FindUniqueElement(
        ImmutableArray<HtmlToken> tokens,
        string source,
        string name)
    {
        int openingIndex = -1;
        for (int index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Kind == HtmlTokenKind.StartTag &&
                tokens[index].NameEquals(source, name))
            {
                if (openingIndex >= 0)
                {
                    return null;
                }

                openingIndex = index;
            }
        }

        if (openingIndex < 0)
        {
            return null;
        }

        int depth = 0;
        for (int index = openingIndex; index < tokens.Length; index++)
        {
            HtmlToken token = tokens[index];
            if (!token.NameEquals(source, name))
            {
                continue;
            }

            if (token.Kind == HtmlTokenKind.StartTag && !token.SelfClosing)
            {
                depth++;
            }
            else if (token.Kind == HtmlTokenKind.EndTag)
            {
                depth--;
                if (depth == 0)
                {
                    (int openStart, int openLength) =
                        tokens[openingIndex].SourceRange.GetOffsetAndLength(source.Length);
                    (int closeStart, int closeLength) =
                        token.SourceRange.GetOffsetAndLength(source.Length);
                    return new ElementBounds(
                        openStart,
                        openStart + openLength,
                        closeStart,
                        closeStart + closeLength);
                }
            }
        }

        return null;
    }

    private static bool HasUnbalancedDocumentElement(
        ImmutableArray<HtmlToken> tokens,
        string source,
        string name)
    {
        int depth = 0;
        foreach (HtmlToken token in tokens)
        {
            if (!token.NameEquals(source, name))
            {
                continue;
            }

            if (token.Kind == HtmlTokenKind.StartTag && !token.SelfClosing)
            {
                depth++;
            }
            else if (token.Kind == HtmlTokenKind.EndTag)
            {
                depth--;
                if (depth < 0)
                {
                    return true;
                }
            }
        }

        return depth != 0;
    }

    private static bool HasVisibleContentOutside(
        string source,
        ImmutableArray<HtmlToken> tokens,
        int start,
        int end,
        ImmutableArray<Range> ignored)
    {
        foreach (HtmlToken token in tokens)
        {
            (int tokenStart, int tokenLength) =
                token.SourceRange.GetOffsetAndLength(source.Length);
            int tokenEnd = tokenStart + tokenLength;
            if (tokenEnd <= start || tokenStart >= end ||
                ignored.Any(range =>
                {
                    (int ignoredStart, int ignoredLength) =
                        range.GetOffsetAndLength(source.Length);
                    return tokenStart >= ignoredStart &&
                           tokenEnd <= ignoredStart + ignoredLength;
                }))
            {
                continue;
            }

            if (token.Kind == HtmlTokenKind.Text)
            {
                int visibleStart = Math.Max(start, tokenStart);
                int visibleEnd = Math.Min(end, tokenEnd);
                if (!IsWhiteSpace(
                        source.AsSpan(visibleStart, visibleEnd - visibleStart)))
                {
                    return true;
                }

                continue;
            }

            if (token.Kind is HtmlTokenKind.Comment or
                HtmlTokenKind.Doctype or
                HtmlTokenKind.ProcessingInstruction)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsLeadingMetadataElement(
        string source,
        ImmutableArray<HtmlToken> tokens,
        ElementBounds head)
    {
        foreach (HtmlToken token in tokens)
        {
            (int start, int length) =
                token.SourceRange.GetOffsetAndLength(source.Length);
            if (start >= head.OpenStart)
            {
                return true;
            }

            if (token.Kind == HtmlTokenKind.Text)
            {
                if (!IsWhiteSpace(source.AsSpan(start, length)))
                {
                    return false;
                }
            }
            else if (token.Kind is not HtmlTokenKind.Comment and
                     not HtmlTokenKind.Doctype and
                     not HtmlTokenKind.ProcessingInstruction)
            {
                return false;
            }
        }

        return true;
    }

    private static string AttributeText(
        string source,
        ImmutableArray<HtmlToken> tokens,
        int tagStart)
    {
        HtmlToken token = tokens.First(item =>
        {
            (int start, _) = item.SourceRange.GetOffsetAndLength(source.Length);
            return start == tagStart;
        });
        return token.Attributes(source).ToString();
    }

    private static SanitizedText Sanitize(
        string source,
        CancellationToken cancellationToken)
    {
        if (source.Length == 0)
        {
            return new SanitizedText(source, 0);
        }

        HtmlScanResult scan = HtmlLexicalScanner.Scan(source, cancellationToken);
        StringBuilder output = new(source.Length);
        int position = 0;
        int removed = 0;
        int tokenIndex = 0;
        foreach (HtmlToken token in scan.Tokens)
        {
            if ((tokenIndex++ & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            (int start, int length) =
                token.SourceRange.GetOffsetAndLength(source.Length);
            if (start > position)
            {
                output.Append(source, position, start - position);
            }

            if (token.Kind == HtmlTokenKind.StartTag &&
                (token.NameEquals(source, "base") ||
                 token.NameEquals(source, "meta") &&
                 AttributeValueEquals(source, token, "http-equiv",
                     "content-security-policy")))
            {
                removed += length;
            }
            else if (token.Kind == HtmlTokenKind.StartTag)
            {
                SanitizedText safeTag =
                    RemoveAttribute(token.Source(source).ToString(), "nonce");
                output.Append(safeTag.Value);
                removed += safeTag.RemovedLength;
            }
            else
            {
                output.Append(source, start, length);
            }

            position = start + length;
        }

        if (position < source.Length)
        {
            output.Append(source, position, source.Length - position);
        }

        return new SanitizedText(output.ToString(), removed);
    }

    private static SanitizedText SanitizeAttributeList(string attributes)
    {
        SanitizedText sanitized =
            RemoveAttribute("<x" + attributes + ">", "nonce");
        return new SanitizedText(
            sanitized.Value.Length >= 3
                ? sanitized.Value[2..^1]
                : string.Empty,
            sanitized.RemovedLength);
    }

    private static SanitizedText RemoveAttribute(string tag, string attributeName)
    {
        StringBuilder? output = null;
        int copyFrom = 0;
        int index = 1;
        if (index < tag.Length && tag[index] == '/')
        {
            index++;
        }

        while (index < tag.Length && IsAttributeNameCharacter(tag[index]))
        {
            index++;
        }

        int removed = 0;
        while (index < tag.Length)
        {
            int whitespaceStart = index;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] is '>' or '/')
            {
                break;
            }

            int nameStart = index;
            while (index < tag.Length && IsAttributeNameCharacter(tag[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                index++;
                continue;
            }

            int nameEnd = index;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index < tag.Length && tag[index] == '=')
            {
                index++;
                while (index < tag.Length && char.IsWhiteSpace(tag[index]))
                {
                    index++;
                }

                if (index < tag.Length && tag[index] is '\'' or '"')
                {
                    char quote = tag[index++];
                    while (index < tag.Length && tag[index] != quote)
                    {
                        index++;
                    }

                    if (index < tag.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    while (index < tag.Length &&
                           !char.IsWhiteSpace(tag[index]) &&
                           tag[index] != '>')
                    {
                        index++;
                    }
                }
            }

            if (!tag.AsSpan(nameStart, nameEnd - nameStart).Equals(
                    attributeName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            output ??= new StringBuilder(tag.Length);
            output.Append(tag, copyFrom, whitespaceStart - copyFrom);
            removed += index - whitespaceStart;
            copyFrom = index;
        }

        if (output is null)
        {
            return new SanitizedText(tag, 0);
        }

        output.Append(tag, copyFrom, tag.Length - copyFrom);
        return new SanitizedText(output.ToString(), removed);
    }

    private static RelocatedText RelocateStyles(
        string body,
        CancellationToken cancellationToken)
    {
        HtmlScanResult scan = HtmlLexicalScanner.Scan(body, cancellationToken);
        List<Range> ranges = [];
        List<string> nodes = [];
        int protectedDepth = 0;
        for (int index = 0; index < scan.Tokens.Length; index++)
        {
            if ((index & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            HtmlToken token = scan.Tokens[index];
            bool protectedElement =
                token.NameEquals(body, "template") ||
                token.NameEquals(body, "svg") ||
                token.NameEquals(body, "noscript");
            if (token.Kind == HtmlTokenKind.EndTag && protectedElement)
            {
                protectedDepth = Math.Max(0, protectedDepth - 1);
            }

            if (protectedDepth == 0 &&
                token.Kind == HtmlTokenKind.StartTag &&
                token.NameEquals(body, "style") &&
                index + 1 < scan.Tokens.Length)
            {
                int endIndex = index + 1;
                if (scan.Tokens[endIndex].Kind == HtmlTokenKind.RawText)
                {
                    endIndex++;
                }

                if (endIndex < scan.Tokens.Length &&
                    scan.Tokens[endIndex].Kind == HtmlTokenKind.EndTag &&
                    scan.Tokens[endIndex].NameEquals(body, "style"))
                {
                    AddRelocatedRange(body, token, scan.Tokens[endIndex], ranges, nodes);
                    index = endIndex;
                    continue;
                }
            }
            else if (protectedDepth == 0 &&
                     token.Kind == HtmlTokenKind.StartTag &&
                     token.NameEquals(body, "link") &&
                     AttributeContainsToken(body, token, "rel", "stylesheet"))
            {
                AddRelocatedRange(body, token, token, ranges, nodes);
                continue;
            }

            if (token.Kind == HtmlTokenKind.StartTag &&
                !token.SelfClosing &&
                protectedElement)
            {
                protectedDepth++;
            }
        }

        if (ranges.Count == 0)
        {
            return new RelocatedText(body, string.Empty, 0);
        }

        StringBuilder output = new(body.Length);
        int position = 0;
        int relocatedLength = 0;
        foreach (Range range in ranges)
        {
            (int start, int length) = range.GetOffsetAndLength(body.Length);
            output.Append(body, position, start - position);
            position = start + length;
            relocatedLength += length;
        }

        output.Append(body, position, body.Length - position);
        return new RelocatedText(
            output.ToString(),
            string.Join(System.Environment.NewLine, nodes),
            relocatedLength);
    }

    private static void AddRelocatedRange(
        string source,
        HtmlToken startToken,
        HtmlToken endToken,
        List<Range> ranges,
        List<string> nodes)
    {
        (int start, _) = startToken.SourceRange.GetOffsetAndLength(source.Length);
        (int endStart, int endLength) =
            endToken.SourceRange.GetOffsetAndLength(source.Length);
        int end = endStart + endLength;
        ranges.Add(new Range(start, end));
        nodes.Add(source[start..end]);
    }

    private static bool AttributeContainsToken(
        string source,
        HtmlToken token,
        string name,
        string value)
    {
        string? attributeValue = FindAttributeValue(source, token, name);
        return attributeValue is not null &&
               attributeValue.Split(
                       (char[]?)null,
                       StringSplitOptions.RemoveEmptyEntries)
                   .Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool AttributeValueEquals(
        string source,
        HtmlToken token,
        string name,
        string value) =>
        FindAttributeValue(source, token, name)?.Equals(
            value,
            StringComparison.OrdinalIgnoreCase) == true;

    private static string? FindAttributeValue(
        string source,
        HtmlToken token,
        string attributeName)
    {
        ReadOnlySpan<char> attributes = token.Attributes(source);
        int index = 0;
        while (index < attributes.Length)
        {
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            int nameStart = index;
            while (index < attributes.Length &&
                   IsAttributeNameCharacter(attributes[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                index++;
                continue;
            }

            ReadOnlySpan<char> name = attributes[nameStart..index];
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            if (index >= attributes.Length || attributes[index] != '=')
            {
                if (name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                continue;
            }

            index++;
            while (index < attributes.Length && char.IsWhiteSpace(attributes[index]))
            {
                index++;
            }

            int valueStart;
            int valueEnd;
            if (index < attributes.Length && attributes[index] is '\'' or '"')
            {
                char quote = attributes[index++];
                valueStart = index;
                while (index < attributes.Length && attributes[index] != quote)
                {
                    index++;
                }

                valueEnd = index;
                if (index < attributes.Length)
                {
                    index++;
                }
            }
            else
            {
                valueStart = index;
                while (index < attributes.Length &&
                       !char.IsWhiteSpace(attributes[index]))
                {
                    index++;
                }

                valueEnd = index;
            }

            if (name.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return attributes[valueStart..valueEnd].ToString();
            }
        }

        return null;
    }

    private static bool IsAttributeNameCharacter(char value) =>
        !char.IsWhiteSpace(value) &&
        value is not '=' and not '>' and not '/' and not '\'' and not '"';

    private static bool IsWhiteSpace(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsWhiteSpace(character) && character != '\uFEFF')
            {
                return false;
            }
        }

        return true;
    }
}
