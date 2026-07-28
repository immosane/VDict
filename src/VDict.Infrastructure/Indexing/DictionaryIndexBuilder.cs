using System.Globalization;
using Microsoft.Data.Sqlite;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Infrastructure.DictionaryFormats;

namespace VDict.Infrastructure.Indexing;

public sealed class DictionaryIndexBuilder
{
    private readonly IAppLocalizer _localizer;

    public DictionaryIndexBuilder(IAppLocalizer? localizer = null)
    {
        _localizer = localizer ?? IdentityAppLocalizer.Instance;
    }

    public const string IndexFormatVersion = "vdict-index-v2";

    public DictionaryIndexState GetState(DictionaryDefinition dictionary, string indexesDirectory)
    {
        if (!File.Exists(dictionary.MdxPath) ||
            !DictionaryIndexPath.TryGet(indexesDirectory, dictionary.MdxPath, out string indexPath))
        {
            return DictionaryIndexState.Invalid;
        }

        if (!File.Exists(indexPath))
        {
            return DictionaryIndexState.Missing;
        }

        try
        {
            using SqliteConnection connection = OpenReadOnly(indexPath);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT key, value FROM meta WHERE key IN ('mdx_path', 'mdx_size', 'mdx_mtime_utc_ticks', 'index_format_version');";
            using SqliteDataReader rows = command.ExecuteReader();
            Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
            while (rows.Read())
            {
                metadata[rows.GetString(0)] = rows.GetString(1);
            }

            FileInfo info = new(dictionary.MdxPath);
            return metadata.TryGetValue("index_format_version", out string? version) &&
                   version == IndexFormatVersion &&
                   metadata.TryGetValue("mdx_path", out string? path) &&
                   path.Equals(Path.GetFullPath(dictionary.MdxPath), StringComparison.OrdinalIgnoreCase) &&
                   metadata.TryGetValue("mdx_size", out string? size) &&
                   size == info.Length.ToString(CultureInfo.InvariantCulture) &&
                   metadata.TryGetValue("mdx_mtime_utc_ticks", out string? ticks) &&
                   ticks == info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                ? DictionaryIndexState.Available
                : DictionaryIndexState.Stale;
        }
        catch (SqliteException)
        {
            return DictionaryIndexState.Invalid;
        }
    }

    public async Task<StagedDictionaryIndex> PrepareAsync(
        DictionaryDefinition dictionary,
        string stagingDirectory,
        IProgress<DictionaryIndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dictionary.MdxPath) || !File.Exists(dictionary.MdxPath))
        {
            throw new FileNotFoundException(
                _localizer.Get("Index_MdxNotFound"),
                dictionary.MdxPath);
        }

        Directory.CreateDirectory(stagingDirectory);
        string stagedPath = Path.Combine(
            stagingDirectory,
            $"{dictionary.Id.Value}-{Guid.NewGuid():N}.sqlite3");
        try
        {
            return await Task.Run(
                () => BuildCore(dictionary, stagedPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteSqliteFamily(stagedPath);
            throw;
        }
    }

    public void Promote(StagedDictionaryIndex staged, string indexesDirectory)
    {
        Directory.CreateDirectory(indexesDirectory);
        if (!DictionaryIndexPath.TryGet(indexesDirectory, staged.MdxPath, out string destination))
        {
            throw new InvalidDataException(_localizer.Get("Index_DestinationFailed"));
        }

        if (File.Exists(destination))
        {
            File.Replace(staged.StagedPath, destination, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(staged.StagedPath, destination);
        }
    }

    public static void Cleanup(StagedDictionaryIndex staged) => DeleteSqliteFamily(staged.StagedPath);

    private StagedDictionaryIndex BuildCore(
        DictionaryDefinition dictionary,
        string stagedPath,
        IProgress<DictionaryIndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        using MDictReader reader = new(dictionary.MdxPath, loadKeys: true);
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = stagedPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Execute(
            connection,
            """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE entries(
                word TEXT NOT NULL,
                word_key TEXT NOT NULL,
                key_blob BLOB,
                record_start INTEGER NOT NULL,
                record_length INTEGER NOT NULL);
            """);

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO entries(word, word_key, key_blob, record_start, record_length) VALUES ($word, $key, $blob, $start, $length);";
        SqliteParameter word = insert.Parameters.Add("$word", SqliteType.Text);
        SqliteParameter key = insert.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter blob = insert.Parameters.Add("$blob", SqliteType.Blob);
        SqliteParameter start = insert.Parameters.Add("$start", SqliteType.Integer);
        SqliteParameter length = insert.Parameters.Add("$length", SqliteType.Integer);

        int count = 0;
        foreach (MDictEntry entry in reader.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Word))
            {
                continue;
            }

            word.Value = entry.Word;
            key.Value = entry.Word.Trim().ToLowerInvariant();
            blob.Value = entry.KeyBytes;
            start.Value = entry.RecordStart;
            length.Value = entry.RecordLength;
            insert.ExecuteNonQuery();
            count++;
            if (count % 1000 == 0)
            {
                progress?.Report(new DictionaryIndexProgress(
                    count,
                    reader.Entries.Count,
                    _localizer.Get("Index_Progress_Indexing")));
            }
        }

        if (count == 0)
        {
            throw new InvalidDataException(_localizer.Get("Index_NoEntries"));
        }

        WriteMetadata(connection, transaction, dictionary, count);
        using SqliteCommand createIndex = connection.CreateCommand();
        createIndex.Transaction = transaction;
        createIndex.CommandText = "CREATE INDEX idx_entries_word_key ON entries(word_key);";
        createIndex.ExecuteNonQuery();
        transaction.Commit();

        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand verify = connection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM entries;";
        if (Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != count)
        {
            throw new InvalidDataException(_localizer.Get("Index_CountValidationFailed"));
        }

        progress?.Report(new DictionaryIndexProgress(
            count,
            count,
            _localizer.Get("Index_Progress_Complete")));
        return new StagedDictionaryIndex(
            dictionary.Id,
            dictionary.MdxPath,
            stagedPath,
            count);
    }

    private static void WriteMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DictionaryDefinition dictionary,
        int count)
    {
        FileInfo info = new(dictionary.MdxPath);
        Dictionary<string, string> values = new()
        {
            ["mdx_path"] = Path.GetFullPath(dictionary.MdxPath),
            ["mdx_size"] = info.Length.ToString(CultureInfo.InvariantCulture),
            ["mdx_mtime_utc_ticks"] = info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            ["index_format_version"] = IndexFormatVersion,
            ["entry_count"] = count.ToString(CultureInfo.InvariantCulture)
        };

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value);";
        SqliteParameter key = command.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter value = command.Parameters.Add("$value", SqliteType.Text);
        foreach (KeyValuePair<string, string> item in values)
        {
            key.Value = item.Key;
            value.Value = item.Value;
            command.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenReadOnly(string path) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void DeleteSqliteFamily(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public enum DictionaryIndexState
{
    Missing,
    Stale,
    Available,
    Invalid
}

public sealed record StagedDictionaryIndex(
    DictionaryId DictionaryId,
    string MdxPath,
    string StagedPath,
    int EntryCount);

public sealed record DictionaryIndexProgress(int Completed, int Total, string Message);
