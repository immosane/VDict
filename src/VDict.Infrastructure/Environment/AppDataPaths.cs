using VDict.Core.Environment;

namespace VDict.Infrastructure.Environment;

public sealed class PortableAppDataPaths : IAppDataPaths
{
    public PortableAppDataPaths(IAppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsPortable)
        {
            throw new ArgumentException("环境不是便携模式。", nameof(environment));
        }

        DataRoot = Path.Combine(environment.ExecutableDirectory, "Data");
        SettingsDirectory = Path.Combine(DataRoot, "Settings");
        string cache = Path.Combine(DataRoot, "Cache");
        SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
        SettingsBackupFile = Path.Combine(SettingsDirectory, "settings.json.bak");
        IndexesDirectory = Path.Combine(cache, "Indexes");
        ResourcesDirectory = Path.Combine(cache, "Resources");
        WebView2Directory = Path.Combine(DataRoot, "WebView2");
        LogsDirectory = Path.Combine(DataRoot, "Logs");
        TempDirectory = Path.Combine(DataRoot, "Temp");
    }

    public string SettingsDirectory { get; }

    public string SettingsFile { get; }

    public string SettingsBackupFile { get; }

    public string IndexesDirectory { get; }

    public string ResourcesDirectory { get; }

    public string WebView2Directory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string DataRoot { get; }

    public void EnsureCreated()
    {
        try
        {
            foreach (string directory in Directories())
            {
                Directory.CreateDirectory(directory);
            }

            VerifyWritable(DataRoot);
        }
        catch (PortableDataDirectoryNotWritableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableDataDirectoryNotWritableException(DataRoot, exception);
        }
    }

    private IEnumerable<string> Directories()
    {
        yield return DataRoot;
        yield return SettingsDirectory;
        yield return IndexesDirectory;
        yield return ResourcesDirectory;
        yield return WebView2Directory;
        yield return LogsDirectory;
        yield return TempDirectory;
    }

    private static void VerifyWritable(string directory)
    {
        string probe = Path.Combine(directory, $".vdict-write-test-{Guid.NewGuid():N}");
        try
        {
            using FileStream stream = new(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PortableDataDirectoryNotWritableException(directory, exception);
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
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

public sealed class PackagedAppDataPaths : IAppDataPaths
{
    public PackagedAppDataPaths(string localState, string localCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localState);
        ArgumentException.ThrowIfNullOrWhiteSpace(localCache);
        DataRoot = Path.GetFullPath(localState);
        SettingsDirectory = DataRoot;
        SettingsFile = Path.Combine(localState, "settings.json");
        SettingsBackupFile = Path.Combine(localState, "settings.json.bak");
        IndexesDirectory = Path.Combine(localCache, "Indexes");
        ResourcesDirectory = Path.Combine(localCache, "Resources");
        WebView2Directory = Path.Combine(localCache, "WebView2");
        LogsDirectory = Path.Combine(localCache, "Logs");
        TempDirectory = Path.Combine(localCache, "Temp");
    }

    public string SettingsDirectory { get; }

    public string SettingsFile { get; }

    public string SettingsBackupFile { get; }

    public string IndexesDirectory { get; }

    public string ResourcesDirectory { get; }

    public string WebView2Directory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string DataRoot { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(IndexesDirectory);
        Directory.CreateDirectory(ResourcesDirectory);
        Directory.CreateDirectory(WebView2Directory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}

public sealed class PortableDataDirectoryNotWritableException : IOException
{
    public PortableDataDirectoryNotWritableException(string directory, Exception innerException)
        : base($"便携版数据目录不可写：{directory}。请将 VDict 移动到具有写入权限的目录。", innerException)
    {
        Directory = directory;
    }

    public string Directory { get; }
}
