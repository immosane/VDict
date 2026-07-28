using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Settings;

namespace VDict.Infrastructure.Settings;

public sealed class AtomicSettingsStore(
    IAppDataPaths paths,
    IAppLocalizer? appLocalizer = null)
{
    private readonly IAppLocalizer _localizer =
        appLocalizer ?? IdentityAppLocalizer.Instance;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        SettingsReadAttempt primary = await TryReadAsync(paths.SettingsFile, cancellationToken)
            .ConfigureAwait(false);
        if (primary.Migration is not null)
        {
            return new SettingsLoadResult(primary.Migration.Settings, primary.Migration, null, null);
        }

        SettingsReadAttempt backup = await TryReadAsync(paths.SettingsBackupFile, cancellationToken)
            .ConfigureAwait(false);
        if (backup.Migration is not null)
        {
            string? corruptCopy = await PreserveCorruptPrimaryAsync(cancellationToken).ConfigureAwait(false);
            return new SettingsLoadResult(
                backup.Migration.Settings,
                backup.Migration,
                _localizer.Get("Settings_Load_RecoveredBackup"),
                corruptCopy);
        }

        string? preserved = await PreserveCorruptPrimaryAsync(cancellationToken).ConfigureAwait(false);
        return new SettingsLoadResult(
            ApplicationSettings.Default,
            new SettingsMigrationResult(0, SettingsSchema.CurrentVersion, [], ApplicationSettings.Default),
            primary.Exists || backup.Exists
                ? _localizer.Get("Settings_Load_DefaultsAfterFailure")
                : null,
            preserved);
    }

    public async Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
        => await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);

    public Task<SettingsLockedSaveResult> TrySaveAsync(
        ApplicationSettings settings,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => TrySaveUnderMutex(settings, expectedRevision, cancellationToken),
            CancellationToken.None);

    private SettingsLockedSaveResult TrySaveUnderMutex(
        ApplicationSettings settings,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        string mutexName = BuildWriteMutexName(paths.SettingsFile);
        using Mutex mutex = new(initiallyOwned: false, mutexName);
        bool acquired = false;
        bool abandoned = false;
        try
        {
            while (!acquired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                    abandoned = true;
                }
            }

            SettingsLoadResult current = LoadAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (current.Settings.Revision != expectedRevision)
            {
                return new SettingsLockedSaveResult(
                    false,
                    current.Settings,
                    abandoned,
                    _localizer.Get("Settings_Save_DiskChanged"));
            }

            SaveCoreAsync(settings, cancellationToken).GetAwaiter().GetResult();
            return new SettingsLockedSaveResult(
                true,
                settings,
                abandoned,
                null);
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private async Task SaveCoreAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        SettingsDocument document = SettingsSchema.FromModel(settings);
        string json = JsonSerializer.Serialize(document, WriteOptions);
        string temporaryPath = Path.Combine(
            paths.SettingsDirectory,
            $".{Path.GetFileName(paths.SettingsFile)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(paths.SettingsFile))
            {
                SettingsReadAttempt currentPrimary =
                    await TryReadAsync(paths.SettingsFile, cancellationToken)
                        .ConfigureAwait(false);
                if (currentPrimary.Migration is null)
                {
                    // Never rotate an unreadable primary over the last known-good backup.
                    File.Replace(
                        temporaryPath,
                        paths.SettingsFile,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Replace(
                        temporaryPath,
                        paths.SettingsFile,
                        paths.SettingsBackupFile,
                        ignoreMetadataErrors: true);
                }
            }
            else
            {
                File.Move(temporaryPath, paths.SettingsFile);
                File.Copy(paths.SettingsFile, paths.SettingsBackupFile, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string BuildWriteMutexName(string settingsFile)
    {
        string fullPath = Path.GetFullPath(settingsFile).ToUpperInvariant();
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        return @"Local\VDict.SettingsWrite." + hash[..24];
    }

    public async Task ExportAsync(
        string destinationPath,
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(SettingsSchema.FromModel(settings), WriteOptions);
        await File.WriteAllTextAsync(
            destinationPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationSettings> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        SettingsReadAttempt attempt = await TryReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ApplicationSettings settings = attempt.Migration?.Settings
            ?? throw new InvalidDataException(
                _localizer.Get("Settings_Import_Invalid"),
                attempt.Error);
        ImmutableArray<SettingsValidationError> invalidPaths =
            ApplicationSettingsNormalizer.Validate(settings, _localizer).Errors
                .Where(error => error.Code == "path_syntax")
                .ToImmutableArray();
        if (!invalidPaths.IsDefaultOrEmpty)
        {
            throw new InvalidDataException(string.Join(
                System.Environment.NewLine,
                invalidPaths.Select(error => error.Message)));
        }

        return settings;
    }

    private static async Task<SettingsReadAttempt> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new SettingsReadAttempt(false, null, null);
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            SettingsDocument document =
                JsonSerializer.Deserialize<SettingsDocument>(json, ReadOptions) ?? new SettingsDocument();
            SettingsMigrationResult migration = SettingsSchema.Migrate(document);
            return new SettingsReadAttempt(true, migration, null);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new SettingsReadAttempt(true, null, exception);
        }
    }

    private async Task<string?> PreserveCorruptPrimaryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SettingsFile))
        {
            return null;
        }

        string destination = Path.Combine(
            paths.SettingsDirectory,
            $"settings.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
        try
        {
            await using FileStream source = new(
                paths.SettingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream target = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return destination;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record SettingsReadAttempt(
        bool Exists,
        SettingsMigrationResult? Migration,
        Exception? Error);
}

public sealed record SettingsLoadResult(
    ApplicationSettings Settings,
    SettingsMigrationResult Migration,
    string? RecoveryMessage,
    string? PreservedCorruptPath);

public sealed record SettingsLockedSaveResult(
    bool Succeeded,
    ApplicationSettings CurrentSettings,
    bool RecoveredAbandonedMutex,
    string? Error);
