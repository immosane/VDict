using System.Collections.Immutable;

namespace VDict.Core.Models;

public sealed record LookupQuery(
    string Text,
    DictionaryId? SelectedDictionaryId = null,
    long RequestVersion = 0);

public sealed record LookupResult(
    DictionaryId DictionaryId,
    string DictionaryDisplayName,
    string ResolvedWord,
    ImmutableArray<string> Entries);

public sealed record LookupBatchResult(
    ImmutableArray<LookupResult> Results,
    ImmutableArray<string> Notices);

public sealed record SuggestionResult(
    string Query,
    DictionaryId? SelectedDictionaryId,
    long CatalogGeneration,
    ImmutableArray<string> Words);

[Flags]
public enum SettingsChangeKind
{
    None = 0,
    Zoom = 1 << 0,
    ApplicationTheme = 1 << 1,
    ReaderAppearance = 1 << 2,
    DictionaryPresentation = 1 << 3,
    CatalogStructure = 1 << 4,
    SecurityPolicy = 1 << 5,
    Diagnostics = 1 << 6,
    MainWindowLayout = 1 << 7,
    UiLanguage = 1 << 8
}

public readonly record struct SettingsChangeSet(SettingsChangeKind Kinds)
{
    public bool IsEmpty => Kinds == SettingsChangeKind.None;

    public bool RequiresCatalogReload =>
        Has(SettingsChangeKind.CatalogStructure) ||
        Has(SettingsChangeKind.SecurityPolicy);

    public bool Has(SettingsChangeKind kind) => (Kinds & kind) != 0;

    public static SettingsChangeSet None { get; } = new(SettingsChangeKind.None);
}

public sealed record ReaderFrame(
    string FrameId,
    DictionaryId DictionaryId,
    string Html)
{
    public int DictionaryResultIndex { get; init; }

    public int EntryIndex { get; init; }

    public string ResolvedWord { get; init; } = string.Empty;

    public bool BlockNetwork { get; init; } = true;

    public DictionaryAppearance Appearance { get; init; } = DictionaryAppearance.Default;

    public ImmutableArray<string> CustomCssResourcePaths { get; init; } = [];

    public ImmutableArray<string> CustomScriptResourcePaths { get; init; } = [];

    public long CatalogGeneration { get; init; }

    public string ResourceCapability { get; init; } = string.Empty;

    public LocalScriptMode LocalScriptMode { get; init; } = LocalScriptMode.Standard;
}

public sealed record ReaderFrameDescriptor(
    string FrameId,
    DictionaryId DictionaryId,
    string SourceHtml)
{
    public int DictionaryResultIndex { get; init; }

    public int EntryIndex { get; init; }

    public string ResolvedWord { get; init; } = string.Empty;

    public bool BlockNetwork { get; init; } = true;

    public DictionaryAppearance Appearance { get; init; } = DictionaryAppearance.Default;

    public ImmutableArray<string> CustomCssResourcePaths { get; init; } = [];

    public ImmutableArray<string> CustomScriptResourcePaths { get; init; } = [];

    public long CatalogGeneration { get; init; }

    public string ResourceCapability { get; init; } = string.Empty;

    public LocalScriptMode LocalScriptMode { get; init; } = LocalScriptMode.Standard;
}

public sealed record ReaderDocumentDescription(
    string Word,
    string RenderToken,
    ImmutableArray<ReaderFrameDescriptor> Frames,
    ImmutableArray<string> Notices)
{
    public ReaderDocumentText Text { get; init; } = ReaderDocumentText.English;

    public ApplicationAppearance Appearance { get; init; } = ApplicationAppearance.Default;

    public bool ReduceDictionaryBrightnessInDarkMode { get; init; } = true;

    public ExternalLinkPolicy ExternalLinkPolicy { get; init; } = ExternalLinkPolicy.Block;

    public string TargetFragment { get; init; } = string.Empty;

    public long CatalogGeneration { get; init; }
}

public sealed record ReaderDocument(
    string Word,
    string Html,
    string RenderToken,
    ImmutableArray<ReaderFrame> Frames,
    ImmutableArray<string> Notices)
{
    public ApplicationAppearance Appearance { get; init; } = ApplicationAppearance.Default;

    public bool ReduceDictionaryBrightnessInDarkMode { get; init; } = true;

    public ExternalLinkPolicy ExternalLinkPolicy { get; init; } = ExternalLinkPolicy.Block;

    public string TargetFragment { get; init; } = string.Empty;

    public long CatalogGeneration { get; init; }

    public ReaderBuildDiagnostics? BuildDiagnostics { get; init; }
}

public sealed record ReaderBuildDiagnostics(
    double EntryRewriteMilliseconds,
    double DocumentAssemblyMilliseconds,
    double TotalMilliseconds,
    int EntryCount,
    int SourceCharacterCount,
    int RewrittenCharacterCount);

public sealed record ResourceContent(string MimeType, ReadOnlyMemory<byte> Body);

public sealed record ResourceReadDiagnostics(
    long SourceBytesRead,
    long RecordBlockBytesDecoded,
    long ResourceBytesMaterialized,
    int ResourceArrayCopyCount,
    bool DecodedResourceCacheHit,
    bool? RecordBlockCacheHit)
{
    public static ResourceReadDiagnostics None { get; } = new(
        0,
        0,
        0,
        0,
        false,
        null);
}

public sealed record ReaderDocumentText(
    string NotFoundTitle,
    string NotFoundPrefix,
    string NotFoundSuffix)
{
    public static ReaderDocumentText English { get; } = new(
        "Entry not found",
        "No dictionary contains ",
        ". Check the spelling or try another keyword.");
}

public sealed record ResourceResolution(
    ResourceContent? Resource,
    string NormalizedPath,
    string Source,
    string? FailureReason)
{
    public string? LookupKey { get; init; }

    public double? MddInitializationMilliseconds { get; init; }

    public bool? PersistentIndexHit { get; init; }

    public ResourceReadDiagnostics ReadDiagnostics { get; init; } =
        ResourceReadDiagnostics.None;
}

public sealed record IndexBuildResult(
    DictionaryId DictionaryId,
    bool Succeeded,
    string? StagedIndexPath,
    string? Error);

public sealed record SettingsValidationError(
    string Path,
    string Code,
    string Message,
    DictionaryId? DictionaryId = null);

public sealed record SettingsValidationResult(ImmutableArray<SettingsValidationError> Errors)
{
    public bool IsValid => Errors.IsDefaultOrEmpty;

    public static SettingsValidationResult Valid { get; } = new([]);
}

public sealed record DictionaryPreparationError(DictionaryId? DictionaryId, string Message);

public sealed record SettingsCommitPlan(
    ApplicationSettings Settings,
    long BaseRevision,
    ImmutableArray<IndexBuildResult> IndexChanges,
    object ImplementationToken)
{
    public long? DiskBaseRevision { get; init; }
}

public sealed record SettingsPrepareResult(
    SettingsCommitPlan? Plan,
    ImmutableArray<DictionaryPreparationError> Errors)
{
    public bool Succeeded => Plan is not null && Errors.IsDefaultOrEmpty;
}

public sealed record SettingsCommitResult(
    bool Succeeded,
    long Revision,
    long CatalogGeneration,
    string? Error)
{
    public ApplicationSettings? CurrentSettings { get; init; }
}

public sealed record SettingsOperationProgress(
    string Phase,
    string Message,
    int Completed,
    int Total,
    DictionaryId? DictionaryId = null,
    string? DictionaryDisplayName = null)
{
    public bool IsIndeterminate => Total <= 0;
}
