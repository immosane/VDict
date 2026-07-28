namespace VDict.Core.Environment;

public interface IAppDataPaths
{
    string SettingsDirectory { get; }

    string SettingsFile { get; }

    string SettingsBackupFile { get; }

    string IndexesDirectory { get; }

    string ResourcesDirectory { get; }

    string WebView2Directory { get; }

    string LogsDirectory { get; }

    string TempDirectory { get; }

    string DataRoot { get; }

    void EnsureCreated();
}

public interface IFontCatalogService
{
    IReadOnlyList<string> GetInstalledFontFamilies();
}

public enum AppLogLevel
{
    Information,
    Warning,
    Error
}

public interface IAppLogger
{
    void Write(
        AppLogLevel level,
        string source,
        string message,
        Exception? exception = null);
}

public interface IAppEnvironment
{
    bool IsPackaged { get; }

    bool IsPortable { get; }

    string ExecutableDirectory { get; }

    string Version { get; }
}
