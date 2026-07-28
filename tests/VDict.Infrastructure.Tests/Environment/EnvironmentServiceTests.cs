using System.Text;
using VDict.Core.Environment;
using VDict.Infrastructure.Environment;

namespace VDict.Infrastructure.Tests.Environment;

public sealed class EnvironmentServiceTests
{
    [Fact]
    public void DiagnosticsLogWriter_WritesBoundedUtf8SessionLog()
    {
        using TemporaryDirectory temporary = new();
        PackagedAppDataPaths paths = new(
            Path.Combine(temporary.Path, "State"),
            Path.Combine(temporary.Path, "Cache"));
        using SessionLog session = new(paths);
        using DiagnosticsLogWriter writer = new(session);

        writer.Write(
            AppLogLevel.Error,
            "测试",
            "第一行\r\n第二行",
            new InvalidOperationException("示例"));
        writer.Dispose();

        string log = Assert.Single(Directory.EnumerateFiles(paths.LogsDirectory, "session-*.log"));
        string content = File.ReadAllText(log, Encoding.UTF8);
        Assert.Contains("[Error] [测试]", content);
        Assert.Contains("第一行 ↩ 第二行", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public void DiagnosticsLogWriter_StandardRedactsSensitiveFieldsAndAbsolutePaths()
    {
        using TemporaryDirectory temporary = new();
        PackagedAppDataPaths paths = new(
            Path.Combine(temporary.Path, "State"),
            Path.Combine(temporary.Path, "Cache"));
        DiagnosticsPrivacySettings privacy = new();
        using SessionLog session = new(paths);
        using DiagnosticsLogWriter writer = new(session, privacy);

        writer.Write(
            AppLogLevel.Information,
            "reader",
            @"Query=private term; Uri=https://dict.invalid/secret; File=C:\Users\name\dict.mdx");
        writer.Dispose();

        string log = Assert.Single(Directory.EnumerateFiles(paths.LogsDirectory, "session-*.log"));
        string content = File.ReadAllText(log, Encoding.UTF8);
        Assert.DoesNotContain("private term", content);
        Assert.DoesNotContain("dict.invalid/secret", content);
        Assert.DoesNotContain(@"C:\Users\name", content);
        Assert.Contains("<redacted>", content);
        Assert.Contains("<local-path>", content);
    }

    [Fact]
    public void DiagnosticsLogWriter_DetailedPreservesExplicitDiagnosticContent()
    {
        using TemporaryDirectory temporary = new();
        PackagedAppDataPaths paths = new(
            Path.Combine(temporary.Path, "State"),
            Path.Combine(temporary.Path, "Cache"));
        DiagnosticsPrivacySettings privacy = new() { DetailedEnabled = true };
        using SessionLog session = new(paths);
        using DiagnosticsLogWriter writer = new(session, privacy);

        writer.Write(AppLogLevel.Information, "reader", @"Query=private; Path=C:\dict\a.mdx");
        writer.Dispose();

        string log = Assert.Single(Directory.EnumerateFiles(paths.LogsDirectory, "session-*.log"));
        string content = File.ReadAllText(log, Encoding.UTF8);
        Assert.Contains("Query=private", content);
        Assert.Contains(@"C:\dict\a.mdx", content);
    }

    [Fact]
    public void SessionLog_EnforcesRetentionAndSessionCapacity()
    {
        using TemporaryDirectory temporary = new();
        PackagedAppDataPaths paths = new(
            Path.Combine(temporary.Path, "State"),
            Path.Combine(temporary.Path, "Cache"));
        paths.EnsureCreated();
        string stale = Path.Combine(paths.LogsDirectory, "session-stale.log");
        File.WriteAllText(stale, "stale", Encoding.UTF8);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10));
        SessionLogOptions options = new(
            TimeSpan.FromDays(2),
            MaximumTotalBytes: 512,
            MaximumSessionBytes: 128);

        using (SessionLog session = new(paths, options))
        {
            for (int index = 0; index < 20; index++)
            {
                session.Append(new string('x', 40));
            }
        }

        Assert.False(File.Exists(stale));
        string current = Assert.Single(
            Directory.EnumerateFiles(paths.LogsDirectory, "session-*.log"));
        Assert.True(new FileInfo(current).Length <= options.MaximumSessionBytes);
    }

    [Fact]
    public void FontCatalog_AlwaysIncludesSafeWindowsFallbacks()
    {
        WindowsFontCatalogService fonts = new();

        IReadOnlyList<string> names = fonts.GetInstalledFontFamilies();

        Assert.Contains("Segoe UI", names);
        Assert.Contains("Microsoft YaHei UI", names);
        Assert.Equal(
            names.Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
            names.Count);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "VDict.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
