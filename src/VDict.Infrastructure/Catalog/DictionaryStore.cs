using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Core.Text;
using VDict.Infrastructure.DictionaryFormats;
using VDict.Infrastructure.Indexing;

namespace VDict.Infrastructure.Catalog;

internal sealed class DictionaryStore : IAsyncDisposable
{
    private readonly object _leaseSync = new();
    private readonly object _readerSync = new();
    private const int ResourceRewriteVersion = 1;
    private static readonly Regex CssImportPattern = new(
        @"@import\s+(?:url\(\s*)?(?:(['""])(.*?)\1|([^'""\)\s;]+))\s*\)?\s*([^;]*);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly DictionaryMemoryCache _memoryCache;
    private readonly DictionaryResourceCache _recordBlockCache;
    private readonly string _persistentResourceCacheDirectory;
    private readonly MddProfileManifest _mddProfiles;
    private readonly Dictionary<string, FileStamp> _sourceStamps;
    private readonly List<MDictReader> _mddReaders = [];
    private readonly TaskCompletionSource _leasesReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MDictReader? _mdxReader;
    private string? _loadError;
    private int _activeLeases;
    private bool _retiring;
    private bool _disposed;

    private DictionaryStore(
        DictionaryDefinition definition,
        string indexPath,
        MDictReader? mdxReader,
        DictionaryResourceCache recordBlockCache,
        DictionaryMemoryCache memoryCache,
        string persistentResourceCacheDirectory,
        string? loadError = null)
    {
        Definition = definition;
        IndexPath = indexPath;
        _mdxReader = mdxReader;
        _loadError = loadError;
        _recordBlockCache = recordBlockCache;
        _memoryCache = memoryCache;
        _persistentResourceCacheDirectory = persistentResourceCacheDirectory;
        _mddProfiles = new MddProfileManifest(indexPath);
        _sourceStamps = CaptureSourceStamps(definition, indexPath);
    }

    public DictionaryDefinition Definition { get; private set; }

    public string IndexPath { get; }

    public string? LoadError => Volatile.Read(ref _loadError);

    public bool CanReuseFor(
        DictionaryDefinition definition,
        string indexPath) =>
        DefinitionsEquivalent(Definition, definition) &&
        Path.GetFullPath(IndexPath).Equals(
            Path.GetFullPath(indexPath),
            StringComparison.OrdinalIgnoreCase) &&
        SourceStampsMatch(definition, indexPath);

    public void ApplyPresentation(DictionaryDefinition definition)
    {
        if (Definition.Id != definition.Id ||
            !Definition.Name.Equals(definition.Name, StringComparison.Ordinal) ||
            !Path.GetFullPath(Definition.MdxPath).Equals(
                Path.GetFullPath(definition.MdxPath),
                StringComparison.OrdinalIgnoreCase) ||
            Definition.Enabled != definition.Enabled ||
            Definition.BlockNetwork != definition.BlockNetwork ||
            Definition.LocalScriptMode != definition.LocalScriptMode ||
            !Definition.MddPaths.SequenceEqual(
                definition.MddPaths,
                StringComparer.OrdinalIgnoreCase) ||
            !Definition.CssPaths.SequenceEqual(
                definition.CssPaths,
                StringComparer.OrdinalIgnoreCase) ||
            !Definition.ScriptPaths.SequenceEqual(
                definition.ScriptPaths,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "只能在不改变词典资源身份的情况下更新展示设置。");
        }

        Definition = definition;
    }

    public static Task<DictionaryStore> OpenAsync(
        DictionaryDefinition definition,
        string indexesDirectory,
        CancellationToken cancellationToken)
    {
        string resourceCacheDirectory = Path.Combine(indexesDirectory, "Resources");
        DictionaryMemoryCache memoryCache = CreateStandaloneCache(definition.Id);
        return OpenAsync(
            definition,
            indexesDirectory,
            resourceCacheDirectory,
            memoryCache,
            cancellationToken);
    }

    public static Task<DictionaryStore> OpenAsync(
        DictionaryDefinition definition,
        string indexesDirectory,
        string resourceCacheDirectory,
        CancellationToken cancellationToken)
    {
        DictionaryMemoryCache memoryCache = CreateStandaloneCache(definition.Id);
        return OpenAsync(
            definition,
            indexesDirectory,
            resourceCacheDirectory,
            memoryCache,
            cancellationToken);
    }

    internal static Task<DictionaryStore> OpenAsync(
        DictionaryDefinition definition,
        string indexesDirectory,
        string resourceCacheDirectory,
        DictionaryMemoryCache memoryCache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(definition.MdxPath))
        {
            throw new FileNotFoundException("找不到 MDX 文件。", definition.MdxPath);
        }

        if (!DictionaryIndexPath.TryGet(indexesDirectory, definition.MdxPath, out string indexPath) ||
            !File.Exists(indexPath))
        {
            throw new FileNotFoundException("找不到已准备的词典索引。", indexPath);
        }

        return OpenFromIndexAsync(
            definition,
            indexPath,
            resourceCacheDirectory,
            memoryCache,
            cancellationToken);
    }

    internal static Task<DictionaryStore> OpenFromIndexAsync(
        DictionaryDefinition definition,
        string indexPath,
        CancellationToken cancellationToken)
    {
        string resourceCacheDirectory =
            Path.Combine(Path.GetDirectoryName(indexPath) ?? string.Empty, "Resources");
        DictionaryMemoryCache memoryCache = CreateStandaloneCache(definition.Id);
        return OpenFromIndexAsync(
            definition,
            indexPath,
            resourceCacheDirectory,
            memoryCache,
            cancellationToken);
    }

    internal static Task<DictionaryStore> OpenFromIndexAsync(
        DictionaryDefinition definition,
        string indexPath,
        string resourceCacheDirectory,
        CancellationToken cancellationToken)
    {
        DictionaryMemoryCache memoryCache = CreateStandaloneCache(definition.Id);
        return OpenFromIndexAsync(
            definition,
            indexPath,
            resourceCacheDirectory,
            memoryCache,
            cancellationToken);
    }

    internal static Task<DictionaryStore> OpenFromIndexAsync(
        DictionaryDefinition definition,
        string indexPath,
        string resourceCacheDirectory,
        DictionaryMemoryCache memoryCache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(definition.MdxPath))
        {
            throw new FileNotFoundException("找不到 MDX 文件。", definition.MdxPath);
        }

        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException("找不到候选词典索引。", indexPath);
        }

        using (SqliteConnection connection = OpenReadOnly(indexPath))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM entries;";
            _ = command.ExecuteScalar();
        }

        DictionaryResourceCache recordBlockCache = new(memoryCache, definition.Id);
        MDictReader? reader = null;
        string? loadError = null;
        try
        {
            reader = new MDictReader(
                definition.MdxPath,
                loadKeys: false,
                recordBlockCache);
        }
        catch (NotSupportedException exception)
        {
            loadError = exception.Message;
        }

        return Task.FromResult(new DictionaryStore(
            definition,
            indexPath,
            reader,
            recordBlockCache,
            memoryCache,
            resourceCacheDirectory,
            loadError));
    }

    public StoreLease AcquireLease()
    {
        lock (_leaseSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retiring)
            {
                throw new InvalidOperationException("词典 Store 已进入退役状态。");
            }

            _activeLeases++;
            return new StoreLease(this);
        }
    }

    public async Task<ImmutableArray<string>> SuggestPrefixAsync(
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        string prefix = text.Trim().ToLowerInvariant();
        if (prefix.Length == 0 || limit <= 0)
        {
            return [];
        }

        await using SqliteConnection connection = OpenReadOnly(IndexPath);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        if (TryGetExclusivePrefixUpperBound(prefix, out string upper))
        {
            command.CommandText = """
                SELECT word
                FROM entries
                WHERE word_key >= $lower AND word_key < $upper
                GROUP BY word_key
                ORDER BY word_key
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$upper", upper);
        }
        else
        {
            command.CommandText = """
                SELECT word
                FROM entries
                WHERE word_key >= $lower
                GROUP BY word_key
                ORDER BY word_key
                LIMIT $limit
                """;
        }

        command.Parameters.AddWithValue("$lower", prefix);
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadWordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<string>> SuggestContainsAsync(
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        string value = text.Trim().ToLowerInvariant();
        if (value.Length < 2 || limit <= 0)
        {
            return [];
        }

        string escaped = EscapeLikePattern(value);
        await using SqliteConnection connection = OpenReadOnly(IndexPath);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT word
            FROM entries
            WHERE word_key LIKE $contains ESCAPE '\'
              AND word_key NOT LIKE $prefix ESCAPE '\'
            GROUP BY word_key
            ORDER BY word_key
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$contains", "%" + escaped + "%");
        command.Parameters.AddWithValue("$prefix", escaped + "%");
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadWordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LookupResult?> LookupAsync(
        string word,
        CancellationToken cancellationToken)
    {
        if (_mdxReader is null)
        {
            return null;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        (string ResolvedWord, ImmutableArray<string> Entries) result;
        try
        {
            result = await LookupCoreAsync(word, 0, seen, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            Volatile.Write(ref _loadError, exception.Message);
            return null;
        }

        return result.Entries.IsDefaultOrEmpty
            ? null
            : new LookupResult(
                Definition.Id,
                Definition.EffectiveDisplayName,
                result.ResolvedWord,
                result.Entries);
    }

    public Task<ResourceResolution> ResolveResourceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ResourcePathPolicy.TryNormalize(path, out string normalized))
        {
            return Task.FromResult(new ResourceResolution(
                null,
                string.Empty,
                "none",
                "资源路径无效或试图越出词典目录。"));
        }

        if (TryResolveCustomCss(normalized, out ResourceResolution? customCss))
        {
            return Task.FromResult(customCss!);
        }

        if (TryResolveCustomScript(normalized, out ResourceResolution? customScript))
        {
            return Task.FromResult(customScript!);
        }

        if (TryResolveCustomCssResource(
                normalized,
                out ResourceResolution? customResource,
                out string? dictionaryFallbackPath))
        {
            return Task.FromResult(customResource!);
        }

        if (dictionaryFallbackPath is not null)
        {
            normalized = dictionaryFallbackPath;
        }

        // Fast path: try cache lookups outside the lock (cache is thread-safe)
        string? localPath = FindSafeLocalFile(normalized);
        if (localPath is not null)
        {
            DecodedResourceCacheKey localCacheKey = CreateResourceCacheKey(localPath, normalized);
            if (_memoryCache.TryGetDecodedResource(
                    Definition.Id, localCacheKey, out ResourceContent? cached) &&
                cached is not null)
            {
                return Task.FromResult(
                    new ResourceResolution(cached, normalized, "memory", null)
                    {
                        ReadDiagnostics = ResourceReadDiagnostics.None with
                        {
                            DecodedResourceCacheHit = true
                        }
                    });
            }
        }

        DictionaryResourceKind resourceKind = MddResourceProfile.Classify(normalized);
        foreach (string mddPath in Definition.MddPaths.Where(File.Exists))
        {
            DecodedResourceCacheKey mddCacheKey = CreateResourceCacheKey(mddPath, normalized);
            if (_memoryCache.TryGetDecodedResource(
                    Definition.Id, mddCacheKey, out ResourceContent? mddCached) &&
                mddCached is not null)
            {
                return Task.FromResult(
                    new ResourceResolution(mddCached, normalized, "memory", null)
                    {
                        ReadDiagnostics = ResourceReadDiagnostics.None with
                        {
                            DecodedResourceCacheHit = true
                        }
                    });
            }
        }

        // Slow path: acquire lock for I/O and reader creation
        lock (_readerSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (localPath is not null)
            {
                DecodedResourceCacheKey localCacheKey = CreateResourceCacheKey(
                    localPath, normalized);
                byte[] bytes = File.ReadAllBytes(localPath);
                long sourceBytesRead = bytes.LongLength;
                string mime = MimeFor(localPath, bytes);
                bytes = RewriteDictionaryCssResource(normalized, mime, bytes);
                ResourceContent resource = new(mime, bytes);
                _memoryCache.StoreDecodedResource(Definition.Id, localCacheKey, resource);
                return Task.FromResult(new ResourceResolution(
                    resource, normalized, "disk",
                    ResourceMimeDiagnostics.DescribeMismatch(normalized, mime, bytes))
                {
                    ReadDiagnostics = new ResourceReadDiagnostics(
                        sourceBytesRead, 0, bytes.LongLength,
                        ResourceArrayCopyCount: 1,
                        DecodedResourceCacheHit: false,
                        RecordBlockCacheHit: null)
                });
            }

            IEnumerable<string> mddPaths = Definition.MddPaths
                .Where(File.Exists)
                .OrderBy(path => MddRouting.Priority(
                    path, resourceKind, _mddProfiles.ProfileFor(path)));
            foreach (string mddPath in mddPaths)
            {
                DecodedResourceCacheKey mddCacheKey = CreateResourceCacheKey(
                    mddPath, normalized);
                if (_memoryCache.IsKnownMissing(Definition.Id, mddCacheKey))
                {
                    continue;
                }

                MDictReader? reader = _mddReaders.FirstOrDefault(existing =>
                    existing.Path.Equals(mddPath, StringComparison.OrdinalIgnoreCase));
                double? initializationMilliseconds = null;
                bool? persistentIndexHit = null;
                if (reader is null)
                {
                    Stopwatch initializationTimer = Stopwatch.StartNew();
                    MddResourceIndex? persisted = MddResourceIndex.Load(
                        _persistentResourceCacheDirectory, mddPath);
                    persistentIndexHit = persisted is not null;
                    if (persisted is not null)
                    {
                        reader = new MDictReader(mddPath, loadKeys: false, _recordBlockCache, persisted);
                    }
                    else
                    {
                        reader = new MDictReader(
                            mddPath, loadKeys: true, _recordBlockCache,
                            keyLoadMode: MDictKeyLoadMode.CompactResourcesOnly);
                        reader.ResourceIndex.Save(_persistentResourceCacheDirectory, mddPath);
                        _mddProfiles.Update(mddPath, reader.ResourceProfile);
                    }

                    _mddReaders.Add(reader);
                    initializationTimer.Stop();
                    initializationMilliseconds = initializationTimer.Elapsed.TotalMilliseconds;
                }

                byte[]? body = reader.ReadResource(
                    normalized,
                    out string? lookupKey,
                    out MDictRecordReadDiagnostics mddRead);
                if (body is null)
                {
                    _memoryCache.MarkMissing(Definition.Id, mddCacheKey);
                    continue;
                }

                string mime = MimeFor(normalized, body);
                body = RewriteDictionaryCssResource(normalized, mime, body);
                ResourceContent resource = new(mime, body);
                _memoryCache.StoreDecodedResource(
                    Definition.Id,
                    mddCacheKey,
                    resource);
                return Task.FromResult(new ResourceResolution(
                    resource,
                    normalized,
                    $"mdd:{Path.GetFileName(mddPath)}",
                    ResourceMimeDiagnostics.DescribeMismatch(normalized, mime, body))
                {
                    LookupKey = lookupKey,
                    MddInitializationMilliseconds = initializationMilliseconds,
                    PersistentIndexHit = persistentIndexHit,
                    ReadDiagnostics = new ResourceReadDiagnostics(
                        mddRead.SourceBytesRead,
                        mddRead.RecordBlockBytesDecoded,
                        mddRead.ResourceBytesMaterialized,
                        mddRead.ResourceArrayCopyCount,
                        DecodedResourceCacheHit: false,
                        mddRead.RecordBlockCacheHit)
                });
            }
        }

        return Task.FromResult(new ResourceResolution(
            null,
            normalized,
            "none",
            "本地目录和已配置的 MDD 中均未找到资源。"));
    }

    public async Task RetireAsync()
    {
        lock (_leaseSync)
        {
            if (_retiring)
            {
                return;
            }

            _retiring = true;
            if (_activeLeases == 0)
            {
                _leasesReleased.TrySetResult();
            }
        }

        await _leasesReleased.Task.ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_leaseSync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        lock (_readerSync)
        {
            _mdxReader?.Dispose();
            foreach (MDictReader reader in _mddReaders)
            {
                reader.Dispose();
            }

            _mddReaders.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private DecodedResourceCacheKey CreateResourceCacheKey(
        string sourcePath,
        string normalizedResourcePath) =>
        DecodedResourceCacheKey.Create(
            sourcePath,
            normalizedResourcePath,
            ResourceRewriteVersion,
            $"{Definition.LocalScriptMode}:{Definition.BlockNetwork}");

    private static DictionaryMemoryCache CreateStandaloneCache(DictionaryId dictionaryId)
    {
        DictionaryMemoryCache cache = new();
        cache.AdvanceGeneration(1, [dictionaryId]);
        return cache;
    }

    private async Task<(string ResolvedWord, ImmutableArray<string> Entries)> LookupCoreAsync(
        string word,
        int depth,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        string key = word.Trim().ToLowerInvariant();
        if (key.Length == 0 || depth > 8 || !seen.Add(key))
        {
            return (word, []);
        }

        List<string> entries = [];
        await using SqliteConnection connection = OpenReadOnly(IndexPath);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT record_start, record_length
            FROM entries
            WHERE word_key = $key
            ORDER BY rowid
            """;
        command.Parameters.AddWithValue("$key", key);
        await using SqliteDataReader rows = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long start = rows.GetInt64(0);
            long length = rows.GetInt64(1);
            string text;
            lock (_readerSync)
            {
                text = _mdxReader!.ReadTextRecord(start, length);
            }

            entries.Add(text);
        }

        if (entries.Count == 1 && TryReadRedirect(entries[0], out string redirect))
        {
            return await LookupCoreAsync(redirect, depth + 1, seen, cancellationToken).ConfigureAwait(false);
        }

        return (
            word,
            entries
                .Select(RemoveRedirectLine)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToImmutableArray());
    }

    private string? FindSafeLocalFile(string normalized)
    {
        string? rootValue = Path.GetDirectoryName(Definition.MdxPath);
        if (string.IsNullOrWhiteSpace(rootValue))
        {
            return null;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
            string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate) ||
                ContainsReparsePoint(root, candidate))
            {
                return null;
            }

            return candidate;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private bool TryResolveCustomCss(string normalized, out ResourceResolution? resolution)
    {
        resolution = null;
        const string prefix = "vdict-custom-css/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                Path.GetFileNameWithoutExtension(normalized[prefix.Length..]),
                out int index) ||
            index < 0 ||
            index >= Definition.CssPaths.Length)
        {
            return false;
        }

        string path = Definition.CssPaths[index];
        if (!File.Exists(path) || ContainsReparsePointForFile(path))
        {
            resolution = new ResourceResolution(
                null,
                normalized,
                "none",
                "配置的 CSS 文件不可用。");
            return true;
        }

        string rewritten = ReadAndRewriteCustomCss(
            path,
            index,
            string.Empty,
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        resolution = new ResourceResolution(
            new ResourceContent("text/css; charset=utf-8", Encoding.UTF8.GetBytes(rewritten)),
            normalized,
            $"css:{Path.GetFileName(path)}",
            null);
        return true;
    }

    private bool TryResolveCustomCssResource(
        string normalized,
        out ResourceResolution? resolution,
        out string? dictionaryFallbackPath)
    {
        resolution = null;
        dictionaryFallbackPath = null;
        const string prefix = "vdict-custom-css-resource/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = normalized[prefix.Length..].Split('/', 2);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int index) ||
            index < 0 ||
            index >= Definition.CssPaths.Length ||
            !ResourcePathPolicy.TryNormalize(parts[1], out string relative))
        {
            resolution = new ResourceResolution(null, normalized, "none", "CSS 资源路径无效。");
            return true;
        }

        string? cssDirectory = Path.GetDirectoryName(Definition.CssPaths[index]);
        string? file = FindSafeFile(cssDirectory, relative);
        if (file is null)
        {
            // Custom CSS commonly references the same image/font tree that is
            // packaged in an MDD. Preserve local-file precedence, but let the
            // regular dictionary/MDD resolver handle a local miss.
            dictionaryFallbackPath = relative;
            return false;
        }

        byte[] body = Path.GetExtension(file).Equals(".css", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(ReadAndRewriteCustomCss(
                file,
                index,
                relative,
                0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            : File.ReadAllBytes(file);
        resolution = new ResourceResolution(
            new ResourceContent(MimeFor(file, body), body),
            normalized,
            $"css-resource:{Path.GetFileName(file)}",
            null);
        return true;
    }

    private string ReadAndRewriteCustomCss(
        string path,
        int cssIndex,
        string relativePath,
        int depth,
        HashSet<string> visited)
    {
        if (depth > 8 || !visited.Add(Path.GetFullPath(path)))
        {
            return string.Empty;
        }

        string css = File.ReadAllText(path);
        css = CssImportPattern.Replace(
            css,
            match =>
            {
                string import = match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;
                if (Uri.TryCreate(import, UriKind.Absolute, out _))
                {
                    return string.Empty;
                }

                if (!ResourcePathPolicy.TryResolve(
                        new ResourceReferenceRequest(
                            Definition.Id,
                            import,
                            relativePath,
                            ReaderResourceKind.Style,
                            ResourceResolutionSource.CustomCss,
                            ResourceNamespaceKind.CustomCss),
                        out ResourceReference importReference) ||
                    importReference.ReferenceKind != ResourceReferenceKind.Local)
                {
                    return string.Empty;
                }

                string normalized = importReference.NormalizedPath;
                string? root = Path.GetDirectoryName(Definition.CssPaths[cssIndex]);
                string? importedPath = FindSafeFile(root, normalized);
                if (importedPath is null ||
                    !Path.GetExtension(importedPath).Equals(".css", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                string media = match.Groups[4].Value.Trim();
                if (media.StartsWith("layer", StringComparison.OrdinalIgnoreCase) ||
                    media.StartsWith("supports", StringComparison.OrdinalIgnoreCase) ||
                    media.Contains(" supports", StringComparison.OrdinalIgnoreCase))
                {
                    string rewrittenImport = RewriteCustomCssUrl(import, cssIndex, relativePath);
                    return $"@import url(\"{rewrittenImport}\"){(media.Length == 0 ? "" : " " + media)};";
                }

                string imported = ReadAndRewriteCustomCss(
                    importedPath,
                    cssIndex,
                    normalized,
                    depth + 1,
                    visited);
                return media.Length == 0 ? imported : $"@media {media}{{{imported}}}";
            });
        return CssUrlRewriter.Rewrite(
            css,
            value => RewriteCustomCssUrl(value, cssIndex, relativePath));
    }

    private bool TryResolveCustomScript(string normalized, out ResourceResolution? resolution)
    {
        resolution = null;
        const string prefix = "vdict-custom-script/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = normalized[prefix.Length..].Split('/', 2);
        ImmutableArray<string> scripts = DictionaryScriptDiscovery.GetEffectivePaths(Definition);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int index) ||
            index < 0 ||
            index >= scripts.Length ||
            !ResourcePathPolicy.TryNormalize(parts[1], out string relative))
        {
            resolution = new ResourceResolution(
                null,
                normalized,
                "none",
                "JavaScript 资源路径无效。");
            return true;
        }

        string configuredScript = scripts[index];
        string? scriptDirectory = Path.GetDirectoryName(configuredScript);
        string? file = FindSafeFile(scriptDirectory, relative);
        if (file is null || ContainsReparsePointForFile(file))
        {
            resolution = new ResourceResolution(
                null,
                normalized,
                "none",
                "配置的 JavaScript 文件或相对资源不可用。");
            return true;
        }

        byte[] body = File.ReadAllBytes(file);
        resolution = new ResourceResolution(
            new ResourceContent(MimeFor(file, body), body),
            normalized,
            $"script:{Path.GetFileName(file)}",
            null);
        return true;
    }

    private string RewriteCustomCssUrl(string value, int cssIndex, string basePath)
    {
        if (!ResourcePathPolicy.TryResolve(
                new ResourceReferenceRequest(
                    Definition.Id,
                    value,
                    basePath,
                    ReaderResourceKind.Unknown,
                    ResourceResolutionSource.CustomCss,
                    ResourceNamespaceKind.CustomCss),
                out ResourceReference reference))
        {
            return "about:blank";
        }

        if (reference.ReferenceKind is ResourceReferenceKind.Fragment or
            ResourceReferenceKind.Data or ResourceReferenceKind.Blob or
            ResourceReferenceKind.Virtual)
        {
            return reference.OriginalValue;
        }

        if (reference.ReferenceKind == ResourceReferenceKind.External)
        {
            return "about:blank";
        }

        if (reference.ReferenceKind is not (ResourceReferenceKind.Local or ResourceReferenceKind.Sound))
        {
            return "about:blank";
        }

        string escaped = string.Join(
            "/",
            reference.NormalizedPath.Split('/').Select(Uri.EscapeDataString));
        string suffix = reference.Query.Length == 0 ? string.Empty : "?" + reference.Query;
        if (reference.Fragment.Length > 0)
        {
            suffix += "#" + reference.Fragment;
        }

        return $"vdict://resource/{Uri.EscapeDataString(Definition.Id.Value)}/" +
               $"vdict-custom-css-resource/{cssIndex}/{escaped}{suffix}";
    }

    private byte[] RewriteDictionaryCssResource(
        string resourcePath,
        string mime,
        byte[] body)
    {
        if (!mime.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        string css = TextDecoding.Decode(body);
        css = CssImportPattern.Replace(
            css,
            match =>
            {
                string rewritten = RewriteDictionaryCssReference(
                    match.Groups[2].Success
                        ? match.Groups[2].Value
                        : match.Groups[3].Value,
                    resourcePath);
                string media = match.Groups[4].Value.Trim();
                return $"@import url(\"{rewritten}\"){(media.Length == 0 ? "" : " " + media)};";
            });
        css = CssUrlRewriter.Rewrite(
            css,
            value => RewriteDictionaryCssReference(value, resourcePath));
        return Encoding.UTF8.GetBytes(css);
    }

    private string RewriteDictionaryCssReference(string value, string basePath)
    {
        if (!ResourcePathPolicy.TryResolve(
                new ResourceReferenceRequest(
                    Definition.Id,
                    value,
                    basePath,
                    ReaderResourceKind.Unknown,
                    ResourceResolutionSource.DictionaryCss,
                    ResourceNamespaceKind.Dictionary),
                out ResourceReference reference))
        {
            return string.Empty;
        }

        if (reference.ReferenceKind is ResourceReferenceKind.Fragment or
            ResourceReferenceKind.Data or ResourceReferenceKind.Blob or
            ResourceReferenceKind.Virtual)
        {
            return reference.OriginalValue;
        }

        if (reference.ReferenceKind == ResourceReferenceKind.External)
        {
            return "about:blank";
        }

        return reference.ReferenceKind is ResourceReferenceKind.Local or ResourceReferenceKind.Sound
            ? ResourcePathPolicy.BuildLegacyResourceUrl(reference)
            : string.Empty;
    }

    private static bool ContainsReparsePointForFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            return directory is null ||
                   (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                   (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static string? FindSafeFile(string? rootValue, string normalized)
    {
        if (string.IsNullOrWhiteSpace(rootValue))
        {
            return null;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootValue));
            string candidate = Path.GetFullPath(
                Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(candidate) &&
                   !ContainsReparsePoint(root, candidate)
                ? candidate
                : null;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ImmutableArray<string>> ReadWordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string>.Builder output = ImmutableArray.CreateBuilder<string>();
        using CancellationTokenRegistration registration = cancellationToken.Register(command.Cancel);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            output.Add(reader.GetString(0));
        }

        return output.ToImmutable();
    }

    private static bool TryGetExclusivePrefixUpperBound(string prefix, out string upper)
    {
        List<Rune> runes = prefix.EnumerateRunes().ToList();
        for (int index = runes.Count - 1; index >= 0; index--)
        {
            int nextValue = runes[index].Value + 1;
            if (nextValue is >= 0xD800 and <= 0xDFFF)
            {
                nextValue = 0xE000;
            }

            if (nextValue > 0x10FFFF || !Rune.TryCreate(nextValue, out Rune next))
            {
                continue;
            }

            System.Text.StringBuilder builder = new();
            for (int prior = 0; prior < index; prior++)
            {
                builder.Append(runes[prior]);
            }

            builder.Append(next);
            upper = builder.ToString();
            return true;
        }

        upper = string.Empty;
        return false;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static bool TryReadRedirect(string text, out string word)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("@@@LINK=", StringComparison.OrdinalIgnoreCase))
        {
            word = string.Empty;
            return false;
        }

        word = trimmed[8..].Trim();
        return word.Length > 0;
    }

    private static string RemoveRedirectLine(string text) =>
        text.TrimStart().StartsWith("@@@LINK=", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : text.Trim();

    private static SqliteConnection OpenReadOnly(string path) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

    private static string MimeFor(string path, byte[] body)
    {
        string byExtension = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".css" => "text/css",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".gif" => "image/gif",
            ".htm" or ".html" => "text/html",
            ".ico" => "image/x-icon",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".js" => "text/javascript",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".ttf" => "font/ttf",
            ".wav" => "audio/wav",
            ".wma" => "audio/x-ms-wma",
            ".webm" => "video/webm",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
        if (byExtension != "application/octet-stream")
        {
            return byExtension;
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (body.Length >= 8 && body.AsSpan(0, 8).SequenceEqual(png))
        {
            return "image/png";
        }

        if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return byExtension;
    }

    private void ReleaseLease()
    {
        lock (_leaseSync)
        {
            _activeLeases--;
            if (_activeLeases == 0 && _retiring)
            {
                _leasesReleased.TrySetResult();
            }
        }
    }

    private bool SourceStampsMatch(
        DictionaryDefinition definition,
        string indexPath)
    {
        Dictionary<string, FileStamp> current =
            CaptureSourceStamps(definition, indexPath);
        return current.Count == _sourceStamps.Count &&
               current.All(item =>
                   _sourceStamps.TryGetValue(item.Key, out FileStamp expected) &&
                   expected == item.Value);
    }

    private static Dictionary<string, FileStamp> CaptureSourceStamps(
        DictionaryDefinition definition,
        string indexPath)
    {
        Dictionary<string, FileStamp> stamps =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { definition.MdxPath, indexPath }
                     .Concat(definition.MddPaths)
                     .Concat(definition.CssPaths)
                     .Concat(DictionaryScriptDiscovery.GetEffectivePaths(definition)))
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                FileInfo info = new(fullPath);
                stamps[fullPath] = info.Exists
                    ? new(info.Length, info.LastWriteTimeUtc.Ticks)
                    : FileStamp.Missing;
            }
            catch
            {
                stamps[path] = FileStamp.Missing;
            }
        }

        return stamps;
    }

    private static bool DefinitionsEquivalent(
        DictionaryDefinition left,
        DictionaryDefinition right) =>
        left.Id == right.Id &&
        left.Name.Equals(right.Name, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        Path.GetFullPath(left.MdxPath).Equals(
            Path.GetFullPath(right.MdxPath),
            StringComparison.OrdinalIgnoreCase) &&
        left.Enabled == right.Enabled &&
        left.BlockNetwork == right.BlockNetwork &&
        left.LocalScriptMode == right.LocalScriptMode &&
        left.Appearance == right.Appearance &&
        left.MddPaths.SequenceEqual(
            right.MddPaths,
            StringComparer.OrdinalIgnoreCase) &&
        left.CssPaths.SequenceEqual(
            right.CssPaths,
            StringComparer.OrdinalIgnoreCase) &&
        left.ScriptPaths.SequenceEqual(
            right.ScriptPaths,
            StringComparer.OrdinalIgnoreCase);

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks)
    {
        public static FileStamp Missing { get; } = new(-1, -1);
    }

    internal sealed class StoreLease(DictionaryStore owner) : IDisposable
    {
        private DictionaryStore? _owner = owner;

        public DictionaryStore Store => _owner ?? throw new ObjectDisposedException(nameof(StoreLease));

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseLease();
    }
}
