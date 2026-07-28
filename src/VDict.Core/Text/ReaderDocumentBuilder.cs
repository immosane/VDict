using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Core.Text;

public static class ReaderDocumentBuilder
{
    public static ReaderDocument Build(
        string word,
        ImmutableArray<LookupResult> results,
        ApplicationSettings? settings = null,
        ImmutableArray<string> notices = default,
        string targetFragment = "",
        long catalogGeneration = 0,
        ReaderDocumentText? text = null)
        => BuildWithDiagnostics(
            word,
            results,
            settings,
            notices,
            targetFragment,
            catalogGeneration,
            text: text).Document;

    public static ReaderDocumentBuildResult BuildWithDiagnostics(
        string word,
        ImmutableArray<LookupResult> results,
        ApplicationSettings? settings = null,
        ImmutableArray<string> notices = default,
        string targetFragment = "",
        long catalogGeneration = 0,
        CancellationToken cancellationToken = default,
        string? renderToken = null,
        ReaderDocumentText? text = null)
    {
        ReaderDocumentDescription description = Describe(
            word,
            results,
            settings,
            notices,
            targetFragment,
            catalogGeneration,
            renderToken,
            text);
        return Build(description, cancellationToken);
    }

    public static ReaderDocumentDescription Describe(
        string word,
        ImmutableArray<LookupResult> results,
        ApplicationSettings? settings = null,
        ImmutableArray<string> notices = default,
        string targetFragment = "",
        long catalogGeneration = 0,
        string? renderToken = null,
        ReaderDocumentText? text = null)
    {
        string token = string.IsNullOrWhiteSpace(renderToken)
            ? Guid.NewGuid().ToString("N")
            : renderToken;
        ImmutableArray<ReaderFrameDescriptor> frames = results
            .SelectMany((result, dictionaryResultIndex) =>
            {
                DictionaryDefinition? definition = settings?.Dictionaries.FirstOrDefault(
                    item => item.Id == result.DictionaryId);
                string capability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                    result.DictionaryId.Value,
                    catalogGeneration);
                ImmutableArray<string> customCssResourcePaths = definition is null
                    ? []
                    : definition.CssPaths
                        .Select((_, cssIndex) =>
                            DictionaryNetworkPolicy.ResourceUriForDictionary(
                                result.DictionaryId.Value,
                                catalogGeneration,
                                capability,
                                $"vdict-custom-css/{cssIndex}.css"))
                        .ToImmutableArray();
                ImmutableArray<string> customScriptResourcePaths = definition is null
                    ? []
                    : DictionaryScriptDiscovery.GetEffectivePaths(definition)
                        .Select((path, scriptIndex) =>
                            DictionaryNetworkPolicy.ResourceUriForDictionary(
                                result.DictionaryId.Value,
                                catalogGeneration,
                                capability,
                                $"vdict-custom-script/{scriptIndex}/" +
                                Uri.EscapeDataString(Path.GetFileName(path))))
                        .ToImmutableArray();
                return result.Entries
                    .Select((entry, entryIndex) => (Entry: entry, EntryIndex: entryIndex))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Entry))
                    .Select(item => new ReaderFrameDescriptor(
                        $"frame-{dictionaryResultIndex:D4}-{item.EntryIndex:D4}",
                        result.DictionaryId,
                        item.Entry)
                    {
                        DictionaryResultIndex = dictionaryResultIndex,
                        EntryIndex = item.EntryIndex,
                        ResolvedWord = result.ResolvedWord,
                        BlockNetwork = definition?.BlockNetwork ?? true,
                        Appearance = definition?.Appearance ?? DictionaryAppearance.Default,
                        CustomCssResourcePaths = customCssResourcePaths,
                        CustomScriptResourcePaths = customScriptResourcePaths,
                        CatalogGeneration = catalogGeneration,
                        ResourceCapability = capability,
                        LocalScriptMode =
                            definition?.LocalScriptMode ?? LocalScriptMode.Standard
                    });
            })
            .ToImmutableArray();

        return new ReaderDocumentDescription(
            word,
            token,
            frames,
            notices.IsDefault ? [] : notices)
        {
            Text = text ?? ReaderDocumentText.English,
            Appearance = settings?.Appearance ?? ApplicationAppearance.Default,
            ReduceDictionaryBrightnessInDarkMode =
                settings?.ReduceDictionaryBrightnessInDarkMode ?? true,
            ExternalLinkPolicy =
                settings?.ExternalLinkPolicy ?? ExternalLinkPolicy.Block,
            TargetFragment = targetFragment,
            CatalogGeneration = catalogGeneration
        };
    }

    public static ReaderDocumentBuildResult Build(
        ReaderDocumentDescription description,
        CancellationToken cancellationToken = default)
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        long rewriteTicks = 0;
        int sourceCharacterCount = description.Frames.Sum(frame => frame.SourceHtml.Length);
        ImmutableArray<ReaderFrame>.Builder framesBuilder =
            ImmutableArray.CreateBuilder<ReaderFrame>(description.Frames.Length);
        foreach (ReaderFrameDescriptor frame in description.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long rewriteStarted = Stopwatch.GetTimestamp();
            ReaderFrame built = BuildFrame(frame, cancellationToken);
            rewriteTicks += Stopwatch.GetTimestamp() - rewriteStarted;
            framesBuilder.Add(built);
        }

        ImmutableArray<ReaderFrame> frames = framesBuilder.MoveToImmutable();
        int rewrittenCharacterCount = frames.Sum(frame => frame.Html.Length);
        long assemblyStarted = Stopwatch.GetTimestamp();
        string body = string.Join(
            System.Environment.NewLine,
            frames.Select(frame =>
                $"<section data-frame=\"{WebUtility.HtmlEncode(frame.FrameId)}\">" +
                $"{frame.Html}</section>"));
        if (frames.IsDefaultOrEmpty)
        {
            string word = WebUtility.HtmlEncode(description.Word);
            ReaderDocumentText text = description.Text;
            body =
                "<section class=\"not-found\" role=\"status\">" +
                "<div class=\"not-found-mark\" aria-hidden=\"true\"></div>" +
                $"<h1>{WebUtility.HtmlEncode(text.NotFoundTitle)}</h1>" +
                $"<p>{WebUtility.HtmlEncode(text.NotFoundPrefix)}" +
                $"<span class=\"not-found-query\">“{word}”</span>" +
                $"{WebUtility.HtmlEncode(text.NotFoundSuffix)}</p></section>";
        }

        double assemblyMilliseconds = Stopwatch.GetElapsedTime(assemblyStarted).TotalMilliseconds;
        totalTimer.Stop();
        ReaderBuildDiagnostics diagnostics = new(
            Stopwatch.GetElapsedTime(0, rewriteTicks).TotalMilliseconds,
            assemblyMilliseconds,
            totalTimer.Elapsed.TotalMilliseconds,
            frames.Length,
            sourceCharacterCount,
            rewrittenCharacterCount);
        ReaderDocument document = new(
            description.Word,
            body,
            description.RenderToken,
            frames,
            description.Notices)
        {
            Appearance = description.Appearance,
            ReduceDictionaryBrightnessInDarkMode =
                description.ReduceDictionaryBrightnessInDarkMode,
            ExternalLinkPolicy =
                description.ExternalLinkPolicy,
            TargetFragment = description.TargetFragment,
            CatalogGeneration = description.CatalogGeneration,
            BuildDiagnostics = diagnostics
        };
        return new ReaderDocumentBuildResult(document, diagnostics);
    }

    public static ReaderFrame BuildFrame(
        ReaderFrameDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string rewritten = RewriteEntry(
            descriptor.SourceHtml,
            descriptor.DictionaryId,
            descriptor.CatalogGeneration,
            descriptor.ResourceCapability,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new ReaderFrame(
            descriptor.FrameId,
            descriptor.DictionaryId,
            rewritten)
        {
            DictionaryResultIndex = descriptor.DictionaryResultIndex,
            EntryIndex = descriptor.EntryIndex,
            ResolvedWord = descriptor.ResolvedWord,
            BlockNetwork = descriptor.BlockNetwork,
            Appearance = descriptor.Appearance,
            CustomCssResourcePaths = descriptor.CustomCssResourcePaths,
            CustomScriptResourcePaths = descriptor.CustomScriptResourcePaths,
            CatalogGeneration = descriptor.CatalogGeneration,
            ResourceCapability = descriptor.ResourceCapability,
            LocalScriptMode = descriptor.LocalScriptMode
        };
    }

    public static ReaderDocument Welcome(ApplicationSettings? settings = null) =>
        new(
            string.Empty,
            BuildWelcomeLogo("welcome"),
            Guid.NewGuid().ToString("N"),
            [],
            [])
        {
            Appearance = settings?.Appearance ?? ApplicationAppearance.Default,
            ReduceDictionaryBrightnessInDarkMode =
                settings?.ReduceDictionaryBrightnessInDarkMode ?? true,
            ExternalLinkPolicy =
                settings?.ExternalLinkPolicy ?? ExternalLinkPolicy.Block
        };

    public static ReaderDocument NoDictionaries(ApplicationSettings? settings = null) =>
        new(
            string.Empty,
            BuildWelcomeLogo("welcome no-dictionaries"),
            Guid.NewGuid().ToString("N"),
            [],
            [])
        {
            Appearance = settings?.Appearance ?? ApplicationAppearance.Default,
            ReduceDictionaryBrightnessInDarkMode =
                settings?.ReduceDictionaryBrightnessInDarkMode ?? true,
            ExternalLinkPolicy =
                settings?.ExternalLinkPolicy ?? ExternalLinkPolicy.Block
        };

    private static string BuildWelcomeLogo(string cssClass) =>
        $"<section class=\"{cssClass}\" aria-label=\"VDict\">" +
        $"<img class=\"welcome-logo\" src=\"{DictionaryNetworkPolicy.WelcomeLogoUri}\" " +
        "alt=\"VDict\"></section>";

    public static string RewriteEntry(
        string html,
        DictionaryId dictionaryId,
        long catalogGeneration = 0,
        string? resourceCapability = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resourceCapability ??= DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
            dictionaryId.Value,
            catalogGeneration);
        string RewriteUrl(string value, string attribute) =>
            RewriteUrlCore(
                value,
                attribute,
                dictionaryId,
                catalogGeneration,
                resourceCapability);

        string RewriteCss(string css) =>
            CssUrlRewriter.Rewrite(
                css,
                value => RewriteUrlCore(
                    value,
                    "src",
                    dictionaryId,
                    catalogGeneration,
                    resourceCapability),
                cancellationToken);

        string rewritten = HtmlAttributeUrlRewriter.Rewrite(
            html,
            RewriteUrl,
            RewriteCss,
            cancellationToken);
        return RewriteStyleElements(rewritten, RewriteCss, cancellationToken);
    }

    private static string RewriteStyleElements(
        string html,
        Func<string, string> rewriteCss,
        CancellationToken cancellationToken = default)
    {
        HtmlScanResult scan = HtmlLexicalScanner.Scan(html, cancellationToken);
        StringBuilder? output = null;
        int position = 0;
        for (int tokenIndex = 0; tokenIndex < scan.Tokens.Length; tokenIndex++)
        {
            if ((tokenIndex & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            HtmlToken token = scan.Tokens[tokenIndex];
            if (token.Kind != HtmlTokenKind.RawText)
            {
                continue;
            }

            if (tokenIndex <= 0 ||
                !scan.Tokens[tokenIndex - 1].NameEquals(html, "style"))
            {
                continue;
            }

            (int start, int length) =
                token.SourceRange.GetOffsetAndLength(html.Length);
            output ??= new StringBuilder(html.Length + 64);
            output.Append(html, position, start - position);
            output.Append(rewriteCss(html.Substring(start, length)));
            position = start + length;
        }

        if (output is null)
        {
            return html;
        }

        output.Append(html, position, html.Length - position);
        return output.ToString();
    }

    private static string RewriteUrlCore(
        string value,
        string attribute,
        DictionaryId dictionaryId,
        long catalogGeneration,
        string resourceCapability)
    {
        string trimmed = WebUtility.HtmlDecode(value).Trim();
        if (trimmed.Length == 0)
        {
            return value;
        }

        ReaderLink link = ReaderLinkClassifier.Classify(trimmed, attribute);
        if (link.Kind == ReaderLinkKind.Fragment)
        {
            return trimmed;
        }

        if (link.Kind == ReaderLinkKind.Entry)
        {
            return "entry://" + Uri.EscapeDataString(link.Target);
        }

        if (link.Kind is ReaderLinkKind.External or ReaderLinkKind.Zoom)
        {
            return trimmed;
        }

        if (link.Kind == ReaderLinkKind.Script)
        {
            return "#";
        }

        if (!ResourcePathPolicy.TryResolve(
                new ResourceReferenceRequest(
                    dictionaryId,
                    trimmed,
                    string.Empty,
                    ReaderResourceKind.Unknown,
                    ResourceResolutionSource.EntryHtml,
                    ResourceNamespaceKind.Dictionary),
                out ResourceReference reference))
        {
            return attribute.Equals("href", StringComparison.OrdinalIgnoreCase)
                ? "#"
                : "about:blank";
        }

        return reference.ReferenceKind switch
        {
            ResourceReferenceKind.Data or ResourceReferenceKind.Blob or
                ResourceReferenceKind.Virtual => trimmed,
            ResourceReferenceKind.Local or ResourceReferenceKind.Sound =>
                DictionaryNetworkPolicy.ResourceUriForDictionary(
                    dictionaryId.Value,
                    catalogGeneration,
                    resourceCapability,
                    reference.NormalizedPath,
                    reference.Query,
                    reference.Fragment),
            _ => attribute.Equals("href", StringComparison.OrdinalIgnoreCase)
                ? "#"
                : "about:blank"
        };
    }
}

public sealed record ReaderDocumentBuildResult(
    ReaderDocument Document,
    ReaderBuildDiagnostics Diagnostics);
