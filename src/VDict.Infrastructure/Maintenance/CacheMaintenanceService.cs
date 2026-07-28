using System.Collections.Immutable;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Infrastructure.Catalog;

namespace VDict.Infrastructure.Maintenance;

[Flags]
public enum CacheCategory
{
    None = 0,
    Indexes = 1 << 0,
    Resources = 1 << 1,
    WebView2 = 1 << 2,
    Logs = 1 << 3,
    Temp = 1 << 4,
    MemoryRecordBlocks = 1 << 5,
    MemoryDecodedResources = 1 << 6,
    NegativeResources = 1 << 7
}

public sealed record CacheCategoryUsage(
    CacheCategory Category,
    string Path,
    long Bytes,
    int FileCount,
    string? Error)
{
    public CacheRuntimeStatistics? Runtime { get; init; }
}

public sealed record CacheRuntimeStatistics(
    long CapacityBytes,
    long Hits,
    long Misses,
    long Evictions,
    long OversizedRejections,
    long PressureTrims);

public sealed record CacheCleanupResult(
    CacheCategory Categories,
    long DeletedBytes,
    int DeletedFiles,
    ImmutableArray<string> Errors,
    SettingsCommitResult? CatalogRefresh);

public sealed class CacheMaintenanceService(
    IAppDataPaths paths,
    DictionaryCatalog catalog,
    IAppLocalizer? appLocalizer = null)
{
    private readonly IAppLocalizer _localizer =
        appLocalizer ?? IdentityAppLocalizer.Instance;

    public Task<ImmutableArray<CacheCategoryUsage>> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            ImmutableArray<CacheCategoryUsage>.Builder output =
                ImmutableArray.CreateBuilder<CacheCategoryUsage>();
            DictionaryMemoryCacheStatistics memory = catalog.GetMemoryCacheStatistics();
            output.Add(ToUsage(CacheCategory.MemoryRecordBlocks, memory.RecordBlocks));
            output.Add(ToUsage(CacheCategory.MemoryDecodedResources, memory.DecodedResources));
            output.Add(new CacheCategoryUsage(
                CacheCategory.NegativeResources,
                "memory",
                0,
                memory.MissingResourceEntries,
                null)
            {
                Runtime = new CacheRuntimeStatistics(
                    0,
                    memory.MissingResourceHits,
                    memory.MissingResourceMisses,
                    0,
                    0,
                    0)
            });
            foreach ((CacheCategory category, string path) in Categories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long bytes = 0;
                int files = 0;
                string? error = null;
                try
                {
                    if (Directory.Exists(path))
                    {
                        foreach (string file in EnumerateFilesSafely(path))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            FileInfo info = new(file);
                            bytes += info.Length;
                            files++;
                        }
                    }
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    error = exception.Message;
                }

                output.Add(new CacheCategoryUsage(category, path, bytes, files, error));
            }

            return output.ToImmutable();
        }, cancellationToken);
    }

    private static CacheCategoryUsage ToUsage(
        CacheCategory category,
        SizedCacheStatistics statistics) =>
        new(
            category,
            "memory",
            statistics.CurrentBytes,
            statistics.EntryCount,
            null)
        {
            Runtime = new CacheRuntimeStatistics(
                statistics.CapacityBytes,
                statistics.Hits,
                statistics.Misses,
                statistics.Evictions,
                statistics.OversizedRejections,
                statistics.PressureTrims)
        };

    public async Task<CacheCleanupResult> CleanAsync(
        CacheCategory categories,
        CancellationToken cancellationToken = default,
        IProgress<SettingsOperationProgress>? progress = null)
    {
        paths.EnsureCreated();
        long deletedBytes = 0;
        int deletedFiles = 0;
        ImmutableArray<string>.Builder errors = ImmutableArray.CreateBuilder<string>();
        SettingsCommitResult? rebuild = null;

        bool indexesSelected = categories.HasFlag(CacheCategory.Indexes);
        bool resourcesSelected = categories.HasFlag(CacheCategory.Resources);
        bool storesMustBeReleased = resourcesSelected;
        if (categories.HasFlag(CacheCategory.WebView2))
        {
            errors.Add(
                _localizer.Get("Maintenance_WebViewProfileRequired"));
        }

        try
        {
            if (storesMustBeReleased)
            {
                progress?.Report(new SettingsOperationProgress(
                    "release",
                    _localizer.Get("Maintenance_Progress_ReleasingIndexes"),
                    0,
                    0));
                await catalog.ReleaseAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach ((CacheCategory category, string path) in Categories())
            {
                if (!categories.HasFlag(category))
                {
                    continue;
                }

                if (category == CacheCategory.WebView2)
                {
                    continue;
                }
                if (category == CacheCategory.Indexes)
                {
                    // RebuildIndexesAsync stages and validates replacement indexes before
                    // releasing the live stores and atomically promoting the replacements.
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    EnsureApprovedDirectory(path);
                    (long Bytes, int Files) deleted = DeleteContentsSafely(
                        path,
                        cancellationToken,
                        errors,
                        category);
                    deletedBytes += deleted.Bytes;
                    deletedFiles += deleted.Files;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add($"{category}: {exception.Message}");
                }
            }

            if (indexesSelected)
            {
                rebuild = storesMustBeReleased
                    ? await catalog.RebuildReleasedIndexesAsync(cancellationToken, progress)
                        .ConfigureAwait(false)
                    : await catalog.RebuildIndexesAsync(cancellationToken, progress)
                        .ConfigureAwait(false);
                if (!rebuild.Succeeded)
                {
                    errors.Add(
                        rebuild.Error ?? _localizer.Get("Maintenance_RebuildFailed"));
                }
            }
            if (!indexesSelected && resourcesSelected)
            {
                rebuild = await catalog.RestoreReleasedStoresAsync(cancellationToken, progress)
                    .ConfigureAwait(false);
                if (!rebuild.Succeeded)
                {
                    errors.Add(rebuild.Error ?? "Dictionary stores could not be restored.");
                }
            }
        }
        catch (OperationCanceledException) when (storesMustBeReleased)
        {
            try
            {
                await catalog.RestoreReleasedStoresAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }

        return new CacheCleanupResult(
            categories,
            deletedBytes,
            deletedFiles,
            errors.ToImmutable(),
            rebuild);
    }

    private IEnumerable<(CacheCategory Category, string Path)> Categories()
    {
        yield return (CacheCategory.Indexes, paths.IndexesDirectory);
        yield return (CacheCategory.Resources, paths.ResourcesDirectory);
        yield return (CacheCategory.WebView2, paths.WebView2Directory);
        yield return (CacheCategory.Logs, paths.LogsDirectory);
        yield return (CacheCategory.Temp, paths.TempDirectory);
    }

    private void EnsureApprovedDirectory(string path)
    {
        string[] approved =
        [
            paths.IndexesDirectory,
            paths.ResourcesDirectory,
            paths.WebView2Directory,
            paths.LogsDirectory,
            paths.TempDirectory
        ];
        CachePathGuard.EnsureApproved(path, approved, _localizer);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    yield return file;
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static (long Bytes, int Files) DeleteContentsSafely(
        string root,
        CancellationToken cancellationToken,
        ImmutableArray<string>.Builder errors,
        CacheCategory category)
    {
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return (0, 0);
        }

        long bytes = 0;
        int files = 0;
        List<string> directories = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string[] filePaths;
            try
            {
                filePaths = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{category}: {directory}: {exception.Message}");
                filePaths = [];
            }

            foreach (string file in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    FileInfo info = new(file);
                    long length = info.Length;
                    File.Delete(file);
                    bytes += length;
                    files++;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{category}: {file}: {exception.Message}");
                }
            }

            string[] children;
            try
            {
                children = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{category}: {directory}: {exception.Message}");
                children = [];
            }

            foreach (string child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(child, recursive: false);
                        continue;
                    }

                    directories.Add(child);
                    pending.Push(child);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{category}: {child}: {exception.Message}");
                }
            }
        }

        foreach (string directory in directories.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{category}: {directory}: {exception.Message}");
                }
            }
        }

        return (bytes, files);
    }
}

internal static class CachePathGuard
{
    public static void EnsureApproved(
        string path,
        IEnumerable<string> approvedPaths,
        IAppLocalizer? localizer = null)
    {
        localizer ??= IdentityAppLocalizer.Instance;
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!approvedPaths.Any(item =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(item))
                    .Equals(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                localizer.Format("Maintenance_UnapprovedDirectory", candidate));
        }

        if (Directory.Exists(candidate) &&
            (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                localizer.Format("Maintenance_ReparsePoint", candidate));
        }
    }
}
