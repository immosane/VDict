using System.Collections.Immutable;
using System.Text.Json;
using VDict.Core.Environment;
using VDict.Core.Models;
using VDict.Infrastructure.Catalog;
using VDict.Infrastructure.Indexing;
using VDict.Infrastructure.Settings;
using VDict.RemediationFixture;

namespace VDict.Infrastructure.Tests.Fixtures;

public sealed class RemediationFixtureTests
{
    [Fact]
    public void Generator_ProducesDeterministicValidatedMdxAndMdd()
    {
        using TemporaryDirectory temporary = new();
        string first = Path.Combine(temporary.Path, "first");
        string second = Path.Combine(temporary.Path, "second");

        FixtureValidationReport firstReport =
            RemediationFixtureGenerator.GenerateAndVerify(first);
        FixtureValidationReport secondReport =
            RemediationFixtureGenerator.GenerateAndVerify(second);

        Assert.Equal(RemediationFixtureGenerator.GeneratorVersion, firstReport.GeneratorVersion);
        Assert.Equal(firstReport, secondReport);
        FixtureManifest firstManifest = ReadManifest(first);
        FixtureManifest secondManifest = ReadManifest(second);
        Assert.Equal(firstManifest.SemanticSha256, secondManifest.SemanticSha256);
        Assert.Equal(
            firstManifest.Files.Select(file => (file.Path, file.Length, file.Sha256)),
            secondManifest.Files.Select(file => (file.Path, file.Length, file.Sha256)));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(first, "vdict-remediation.mdx")),
            File.ReadAllBytes(Path.Combine(second, "vdict-remediation.mdx")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(first, "vdict-remediation.mdd")),
            File.ReadAllBytes(Path.Combine(second, "vdict-remediation.mdd")));
    }

    [Fact]
    public void Manifest_CoversRequiredMachineCheckableMatrix()
    {
        using TemporaryDirectory temporary = new();
        string output = Path.Combine(temporary.Path, "fixture");
        RemediationFixtureGenerator.GenerateAndVerify(output);

        FixtureManifest manifest = ReadManifest(output);
        Assert.Equal("vdict-remediation-0a", manifest.FixtureId);
        Assert.Equal(3, manifest.Entries.Count(entry => entry.Query == "fixture-multi-entry"));
        Assert.Contains(
            manifest.Entries,
            entry => entry.Query == "fixture-script-standard" &&
                     entry.Expected["standard"] == "allowed");
        Assert.Contains(
            manifest.Entries,
            entry => entry.Query == "fixture-script-legacy" &&
                     entry.Expected["standard"] == "blocked");
        Assert.Contains(
            manifest.Entries,
            entry => entry.Query == "fixture-security" &&
                     entry.Expected["network"] == "blocked");
        Assert.Contains(
            manifest.Entries,
            entry => entry.Query == "fixture-audio" &&
                     entry.Features.Contains("mdd-audio"));
        Assert.Contains(
            manifest.Entries,
            entry => entry.Query == "fixture-image" &&
                     entry.Features.Contains("dynamic-image"));
        Assert.DoesNotContain(
            manifest.Entries,
            entry => entry.Query == "fixture-missing");
    }

    [Fact]
    public async Task GeneratedFixture_ImportsQueriesRedirectsAndResolvesBothResourcePaths()
    {
        using TemporaryDirectory temporary = new();
        string fixture = Path.Combine(temporary.Path, "fixture");
        string data = Path.Combine(temporary.Path, "data");
        RemediationFixtureGenerator.GenerateAndVerify(fixture);
        TestPaths paths = new(data);
        paths.EnsureCreated();
        DictionaryId dictionaryId = new("remediation-fixture");
        DictionaryDefinition dictionary = new(
            dictionaryId,
            "VDict Remediation Fixture",
            null,
            Path.Combine(fixture, "vdict-remediation.mdx"),
            true,
            true,
            [Path.Combine(fixture, "vdict-remediation.mdd")],
            [Path.Combine(fixture, "styles", "fixture.css")],
            DictionaryAppearance.Default)
        {
            ScriptPaths = [Path.Combine(fixture, "scripts", "standard.js")]
        };
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Revision = 1,
            Dictionaries = [dictionary]
        };
        AtomicSettingsStore settingsStore = new(paths);
        await settingsStore.SaveAsync(settings);
        await using DictionaryCatalog catalog = new(
            paths,
            settingsStore,
            new DictionaryIndexBuilder());

        await catalog.InitializeAsync();
        LookupResult basic = Assert.Single(
            await catalog.LookupAsync(new LookupQuery("fixture-basic")));
        LookupResult multi = Assert.Single(
            await catalog.LookupAsync(new LookupQuery("fixture-multi-entry")));
        LookupResult redirect = Assert.Single(
            await catalog.LookupAsync(new LookupQuery("fixture-redirect")));
        ResourceResolution local = await catalog.ResolveResourceAsync(
            dictionaryId,
            "assets/local.png");
        ResourceResolution mdd = await catalog.ResolveResourceAsync(
            dictionaryId,
            "mdd-image.png");
        ResourceResolution warmMdd = await catalog.ResolveResourceAsync(
            dictionaryId,
            "mdd-image.png");

        Assert.Contains("fixture-basic-marker", Assert.Single(basic.Entries));
        Assert.Equal(
            new[]
            {
                "fixture-multi-entry-1",
                "fixture-multi-entry-2",
                "fixture-multi-entry-3"
            },
            multi.Entries.Select(FindEntryMarker));
        Assert.Equal("fixture-basic", redirect.ResolvedWord);
        Assert.Contains("fixture-basic-marker", Assert.Single(redirect.Entries));
        Assert.NotNull(local.Resource);
        Assert.Equal("image/png", local.Resource!.MimeType);
        Assert.Equal("disk", local.Source);
        Assert.Equal(local.Resource.Body.Length, local.ReadDiagnostics.SourceBytesRead);
        Assert.Equal(local.Resource.Body.Length, local.ReadDiagnostics.ResourceBytesMaterialized);
        Assert.Equal(1, local.ReadDiagnostics.ResourceArrayCopyCount);
        Assert.False(local.ReadDiagnostics.DecodedResourceCacheHit);
        Assert.NotNull(mdd.Resource);
        Assert.Equal("image/png", mdd.Resource!.MimeType);
        Assert.StartsWith("mdd:", mdd.Source);
        Assert.Equal(local.Resource.Body.ToArray(), mdd.Resource.Body.ToArray());
        Assert.True(mdd.ReadDiagnostics.SourceBytesRead > 0);
        Assert.True(mdd.ReadDiagnostics.RecordBlockBytesDecoded >= mdd.Resource.Body.Length);
        Assert.Equal(mdd.Resource.Body.Length, mdd.ReadDiagnostics.ResourceBytesMaterialized);
        Assert.Equal(1, mdd.ReadDiagnostics.ResourceArrayCopyCount);
        Assert.False(mdd.ReadDiagnostics.DecodedResourceCacheHit);
        Assert.Equal("memory", warmMdd.Source);
        Assert.True(warmMdd.ReadDiagnostics.DecodedResourceCacheHit);
        Assert.Equal(0, warmMdd.ReadDiagnostics.SourceBytesRead);
        Assert.Equal(0, warmMdd.ReadDiagnostics.ResourceBytesMaterialized);
    }

    [Fact]
    public void Generator_RefusesToReplaceUnownedDirectory()
    {
        using TemporaryDirectory temporary = new();
        string output = Path.Combine(temporary.Path, "not-a-fixture");
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "keep.txt");
        File.WriteAllText(sentinel, "user data");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => RemediationFixtureGenerator.GenerateAndVerify(output));

        Assert.Contains("Refusing to replace unowned fixture directory", exception.Message);
        Assert.Equal("user data", File.ReadAllText(sentinel));
        Assert.Empty(
            Directory.EnumerateDirectories(
                temporary.Path,
                "not-a-fixture.staging-*",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Validator_ReportsTheSpecificCorruptFile()
    {
        using TemporaryDirectory temporary = new();
        string output = Path.Combine(temporary.Path, "fixture");
        RemediationFixtureGenerator.GenerateAndVerify(output);
        File.AppendAllText(Path.Combine(output, "styles", "fixture.css"), "corrupt");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => RemediationFixtureValidator.Validate(output));

        Assert.Contains("fixture.css", exception.Message);
        Assert.Contains("mismatch", exception.Message);
    }

    private static FixtureManifest ReadManifest(string output)
    {
        string json = File.ReadAllText(Path.Combine(output, "fixture-manifest.json"));
        return JsonSerializer.Deserialize<FixtureManifest>(json)
            ?? throw new InvalidDataException("Fixture manifest was empty.");
    }

    private static string FindEntryMarker(string html)
    {
        const string prefix = "fixture-multi-entry-";
        int start = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return html.Substring(start, prefix.Length + 1);
    }

    private sealed class TestPaths(string root) : IAppDataPaths
    {
        public string DataRoot { get; } = root;
        public string SettingsDirectory { get; } = Path.Combine(root, "Settings");
        public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
        public string SettingsBackupFile => Path.Combine(SettingsDirectory, "settings.json.bak");
        public string IndexesDirectory { get; } = Path.Combine(root, "Indexes");
        public string ResourcesDirectory { get; } = Path.Combine(root, "Resources");
        public string WebView2Directory { get; } = Path.Combine(root, "WebView2");
        public string LogsDirectory { get; } = Path.Combine(root, "Logs");
        public string TempDirectory { get; } = Path.Combine(root, "Temp");

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "VDict.RemediationFixture.Tests",
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
