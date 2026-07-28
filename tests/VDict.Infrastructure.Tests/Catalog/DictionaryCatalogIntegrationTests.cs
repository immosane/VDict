using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Models;
using VDict.Infrastructure.Catalog;
using VDict.Infrastructure.Indexing;
using VDict.Infrastructure.Maintenance;
using VDict.Infrastructure.Settings;
using VDict.Infrastructure.Tests.Fixtures;
using FormatKeyLoadMode = VDict.Infrastructure.DictionaryFormats.MDictKeyLoadMode;
using FormatReader = VDict.Infrastructure.DictionaryFormats.MDictReader;
using FormatResourceCache = VDict.Infrastructure.DictionaryFormats.DictionaryResourceCache;

namespace VDict.Infrastructure.Tests.Catalog;

public sealed class DictionaryCatalogIntegrationTests
{
    [Fact]
    public void MDictReader_ReadsRecordAcrossRecordBlockBoundary()
    {
        using TemporaryDirectory temporary = new();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "split.mdx");
        byte[] expected = Encoding.UTF8.GetBytes("record-crosses-block-boundary");
        MDictFixture.Write(
            mdxPath,
            [("word", expected), ("tail", Encoding.UTF8.GetBytes("tail"))],
            utf16Keys: false,
            recordBlockSplitOffset: 8);
        using FormatReader reader = new(mdxPath, loadKeys: true);
        VDict.Infrastructure.DictionaryFormats.MDictEntry entry = reader.Entries[0];

        byte[] actual = reader.ReadRecord(entry.RecordStart, entry.RecordLength);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MddTailFallback_RejectsAmbiguousFileNames()
    {
        using TemporaryDirectory temporary = new();
        string mddPath = System.IO.Path.Combine(temporary.Path, "ambiguous.mdd");
        MDictFixture.Write(
            mddPath,
            [
                ("\\one\\same.png", BrowserAssetFixtures.Png),
                ("\\two\\same.png", BrowserAssetFixtures.Png)
            ],
            utf16Keys: true);
        using FormatReader reader = new(
            mddPath,
            loadKeys: true,
            new FormatResourceCache());

        Assert.Null(reader.ReadResource("missing/same.png"));
    }

    [Fact]
    public async Task FileInspector_DiscoversDictionarySidecars()
    {
        using TemporaryDirectory temporary = new();
        string mdx = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        string mdd = System.IO.Path.Combine(temporary.Path, "demo.1.mdd");
        string css = System.IO.Path.Combine(temporary.Path, "demo.dark.css");
        string unrelated = System.IO.Path.Combine(temporary.Path, "other.css");
        string sameStemScript = System.IO.Path.Combine(temporary.Path, "demo.js");
        string unrelatedScript = System.IO.Path.Combine(temporary.Path, "other.js");
        await File.WriteAllTextAsync(mdx, "fixture");
        await File.WriteAllTextAsync(mdd, "fixture");
        await File.WriteAllTextAsync(css, "fixture");
        await File.WriteAllTextAsync(unrelated, "fixture");
        await File.WriteAllTextAsync(sameStemScript, "window.demo=true;");
        await File.WriteAllTextAsync(unrelatedScript, "window.other=true;");

        DictionaryFileInspection result = new DictionaryFileInspector().Inspect(mdx);

        Assert.Equal("demo", result.SuggestedName);
        Assert.Equal(Path.GetFullPath(mdx), result.FullMdxPath);
        Assert.Equal(mdd, Assert.Single(result.MddPaths));
        Assert.Equal(new[] { css, unrelated }, result.CssPaths.ToArray());
        Assert.Equal(sameStemScript, Assert.Single(result.AutoScriptPaths));
    }

    [Fact]
    public async Task FileInspector_UsesHeaderTitleAndSidecarRules()
    {
        using TemporaryDirectory temporary = new();
        string mdx = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        string mdd = System.IO.Path.Combine(temporary.Path, "demo.1.mdd");
        string globalCss = System.IO.Path.Combine(temporary.Path, "global.css");
        MDictFixture.Write(
            mdx,
            [("word", Encoding.UTF8.GetBytes("body"))],
            utf16Keys: false,
            title: "Header Dictionary");
        await File.WriteAllTextAsync(mdd, "fixture");
        await File.WriteAllTextAsync(globalCss, "body{}");

        DictionaryFileInspection result = new DictionaryFileInspector().Inspect(mdx);

        Assert.Equal("Header Dictionary", result.SuggestedName);
        Assert.Equal(mdd, Assert.Single(result.MddPaths));
        Assert.Equal(globalCss, Assert.Single(result.CssPaths));
    }

    [Fact]
    public async Task Catalog_BuildsQueriesRedirectsAndResolvesMddResources()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        string mddPath = System.IO.Path.Combine(temporary.Path, "demo.mdd");
        string cssPath = System.IO.Path.Combine(temporary.Path, "demo.css");
        string cssImagePath = System.IO.Path.Combine(temporary.Path, "css-image.png");
        string importedCssPath = System.IO.Path.Combine(temporary.Path, "nested style.css");
        string fontPath = System.IO.Path.Combine(temporary.Path, "font.woff2");
        string automaticScriptPath = System.IO.Path.Combine(temporary.Path, "demo.js");
        string dependencyScriptPath = System.IO.Path.Combine(temporary.Path, "dependency-sidecar.js");
        string scriptModuleDirectory = System.IO.Path.Combine(temporary.Path, "modules");
        string childScriptPath = System.IO.Path.Combine(scriptModuleDirectory, "child.js");
        MDictFixture.Write(
            mdxPath,
            [
                ("apple", Encoding.UTF8.GetBytes("<p>fruit<img src=\"image.png\"></p>")),
                ("apply", Encoding.UTF8.GetBytes("<p>verb</p>")),
                ("pomaceous", Encoding.UTF8.GetBytes("@@@LINK=apple"))
            ],
            utf16Keys: false);
        byte[] png = BrowserAssetFixtures.Png;
        MDictFixture.Write(
            mddPath,
            [
                ("\\image.png", png),
                ("\\mdd-only.png", png),
                ("\\icon.svg", Encoding.UTF8.GetBytes(BrowserAssetFixtures.Svg)),
                ("\\voice.mp3", BrowserAssetFixtures.Mp3),
                ("\\tone.wav", BrowserAssetFixtures.Wav),
                ("\\fixture.js", Encoding.UTF8.GetBytes(BrowserAssetFixtures.Script)),
                ("\\module.js", Encoding.UTF8.GetBytes(BrowserAssetFixtures.Module)),
                ("\\dependency.js", Encoding.UTF8.GetBytes("export const value = 41;")),
                ("\\data.json", Encoding.UTF8.GetBytes(BrowserAssetFixtures.Json)),
                ("\\clip.mp4", [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]),
                ("\\styles\\main.css", Encoding.UTF8.GetBytes(
                    "@import \"nested style.css\"; .hero{background:url(../image.png)}" +
                    ".root{src:url(/font/a.ttf?v=2#face)}")),
                ("\\styles\\nested style.css", Encoding.UTF8.GetBytes(
                    "@font-face{font-family:mdd;src:url(font.woff2)}")),
                ("\\styles\\font.woff2", BrowserAssetFixtures.Woff2),
                ("\\styles\\font.ttf", BrowserAssetFixtures.Ttf),
                ("\\font\\a.ttf", BrowserAssetFixtures.Ttf)
            ],
            utf16Keys: true);
        await File.WriteAllTextAsync(
            cssPath,
            "@import \"nested style.css\"; .icon{background:url(css-image.png)}" +
            ".mdd-icon{background:url(mdd-only.png)}");
        await File.WriteAllTextAsync(
            importedCssPath,
            "@font-face{font-family:test;src:url(font.woff2)}");
        await File.WriteAllBytesAsync(fontPath, BrowserAssetFixtures.Woff2);
        await File.WriteAllBytesAsync(cssImagePath, png);
        Directory.CreateDirectory(scriptModuleDirectory);
        await File.WriteAllTextAsync(automaticScriptPath, "window.demoSidecar=true;");
        await File.WriteAllTextAsync(dependencyScriptPath, "window.dependencySidecar=true;");
        await File.WriteAllTextAsync(childScriptPath, "window.childSidecar=true;");
        using (FormatReader compactReader = new(
                   mddPath,
                   loadKeys: true,
                   new FormatResourceCache(),
                   keyLoadMode: FormatKeyLoadMode.CompactResourcesOnly))
        {
            Assert.True(compactReader.ResourceIndex.Count > 0);
            Assert.Equal(png, compactReader.ReadResource("image.png"));
        }

        DictionaryDefinition dictionary = new(
            new DictionaryId("demo"),
            "Demo",
            null,
            mdxPath,
            true,
            true,
            [mddPath],
            [cssPath],
            DictionaryAppearance.Default)
        {
            ScriptPaths = [dependencyScriptPath]
        };
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Revision = 7,
            Dictionaries = [dictionary]
        };
        AtomicSettingsStore settingsStore = new(paths);
        await settingsStore.SaveAsync(settings);
        DictionaryIndexBuilder indexBuilder = new();
        await using DictionaryCatalog catalog = new(paths, settingsStore, indexBuilder);

        SettingsLoadResult load = await catalog.InitializeAsync();
        SuggestionResult suggestions = await catalog.SuggestAsync(new LookupQuery("app"), 10);
        ImmutableArray<LookupResult> lookup =
            await catalog.LookupAsync(new LookupQuery("pomaceous"));
        ResourceResolution resource =
            await catalog.ResolveResourceAsync(dictionary.Id, "image.png");
        ResourceResolution cachedResource =
            await catalog.ResolveResourceAsync(dictionary.Id, "image.png");
        ResourceResolution customCss =
            await catalog.ResolveResourceAsync(dictionary.Id, "vdict-custom-css/0.css");
        ResourceResolution customCssImage =
            await catalog.ResolveResourceAsync(
                dictionary.Id,
                "vdict-custom-css-resource/0/css-image.png");
        ResourceResolution customCssMddImage =
            await catalog.ResolveResourceAsync(
                dictionary.Id,
                "vdict-custom-css-resource/0/mdd-only.png");
        ResourceResolution audio = await catalog.ResolveResourceAsync(dictionary.Id, "voice.mp3");
        ResourceResolution wave = await catalog.ResolveResourceAsync(dictionary.Id, "tone.wav");
        ResourceResolution svg = await catalog.ResolveResourceAsync(dictionary.Id, "icon.svg");
        ResourceResolution script = await catalog.ResolveResourceAsync(dictionary.Id, "fixture.js");
        ResourceResolution json = await catalog.ResolveResourceAsync(dictionary.Id, "data.json");
        ResourceResolution video = await catalog.ResolveResourceAsync(dictionary.Id, "clip.mp4");
        ResourceResolution mddCss = await catalog.ResolveResourceAsync(
            dictionary.Id,
            "styles/main.css");
        ResourceResolution manualScript = await catalog.ResolveResourceAsync(
            dictionary.Id,
            "vdict-custom-script/0/dependency-sidecar.js");
        ResourceResolution automaticScript = await catalog.ResolveResourceAsync(
            dictionary.Id,
            "vdict-custom-script/1/demo.js");
        ResourceResolution relativeScript = await catalog.ResolveResourceAsync(
            dictionary.Id,
            "vdict-custom-script/1/modules/child.js");
        ResourceResolution escapedScript = await catalog.ResolveResourceAsync(
            dictionary.Id,
            "vdict-custom-script/1/../outside.js");

        Assert.Equal(7, load.Settings.Revision);
        Assert.Equal(new[] { "apple", "apply" }, suggestions.Words.ToArray());
        LookupResult result = Assert.Single(lookup);
        Assert.Equal("apple", result.ResolvedWord);
        Assert.Contains("fruit", Assert.Single(result.Entries));
        Assert.True(resource.Resource is not null, resource.FailureReason);
        Assert.Equal("image/png", resource.Resource!.MimeType);
        Assert.Equal(png, resource.Resource.Body.ToArray());
        Assert.StartsWith("mdd:", resource.Source);
        Assert.True(resource.MddInitializationMilliseconds.HasValue);
        Assert.True(resource.MddInitializationMilliseconds >= 0);
        Assert.False(resource.PersistentIndexHit);
        Assert.Equal("memory", cachedResource.Source);
        Assert.Null(cachedResource.MddInitializationMilliseconds);
        Assert.Null(cachedResource.PersistentIndexHit);
        Assert.Single(
            Directory.EnumerateFiles(
                paths.ResourcesDirectory,
                "vdict-mdd-*.resource-index"));
        Assert.Contains(
            "vdict-custom-css-resource/0/css-image.png",
            Encoding.UTF8.GetString(customCss.Resource!.Body.Span));
        Assert.Contains(
            "vdict-custom-css-resource/0/font.woff2",
            Encoding.UTF8.GetString(customCss.Resource!.Body.Span));
        Assert.Equal(png, customCssImage.Resource!.Body.ToArray());
        Assert.StartsWith("css-resource:", customCssImage.Source);
        Assert.Equal(png, customCssMddImage.Resource!.Body.ToArray());
        Assert.StartsWith("mdd:", customCssMddImage.Source);
        Assert.Equal("audio/mpeg", audio.Resource!.MimeType);
        Assert.Equal(BrowserAssetFixtures.Mp3, audio.Resource.Body.ToArray());
        Assert.Equal("audio/wav", wave.Resource!.MimeType);
        Assert.Equal(BrowserAssetFixtures.Wav, wave.Resource.Body.ToArray());
        Assert.Equal("image/svg+xml", svg.Resource!.MimeType);
        Assert.Equal("text/javascript", script.Resource!.MimeType);
        Assert.Equal("application/octet-stream", json.Resource!.MimeType);
        Assert.Equal("video/mp4", video.Resource!.MimeType);
        string mddCssText = Encoding.UTF8.GetString(mddCss.Resource!.Body.Span);
        Assert.Contains("vdict://resource/demo/styles/nested%20style.css", mddCssText);
        Assert.Contains("vdict://resource/demo/image.png", mddCssText);
        Assert.Contains("vdict://resource/demo/font/a.ttf?v=2#face", mddCssText);
        Assert.Equal("text/javascript", manualScript.Resource!.MimeType);
        Assert.Contains("dependencySidecar", Encoding.UTF8.GetString(manualScript.Resource.Body.Span));
        Assert.Contains("demoSidecar", Encoding.UTF8.GetString(automaticScript.Resource!.Body.Span));
        Assert.Contains("childSidecar", Encoding.UTF8.GetString(relativeScript.Resource!.Body.Span));
        Assert.Null(escapedScript.Resource);

        DictionaryMemoryCacheStatistics beforeZoom = catalog.GetMemoryCacheStatistics();
        long generationBeforeZoom = catalog.GetSnapshot().Generation;
        SettingsCommitResult zoomCommit = await catalog.UpdateZoomAsync(
            1.15,
            catalog.GetSnapshot().SettingsRevision,
            catalog.GetSettingsSnapshot().Appearance.ContentZoom);
        Assert.True(zoomCommit.Succeeded);
        Assert.Equal(generationBeforeZoom, catalog.GetSnapshot().Generation);
        Assert.Equal(beforeZoom.CurrentBytes, catalog.GetMemoryCacheStatistics().CurrentBytes);

        ApplicationSettings securityUpdate = catalog.GetSettingsSnapshot() with
        {
            Dictionaries =
            [
                catalog.GetSettingsSnapshot().Dictionaries[0] with
                {
                    BlockNetwork = !catalog.GetSettingsSnapshot().Dictionaries[0].BlockNetwork
                }
            ]
        };
        SettingsPrepareResult securityPreparation =
            await catalog.PrepareUpdateAsync(securityUpdate);
        SettingsCommitResult securityCommit =
            await catalog.CommitUpdateAsync(securityPreparation.Plan!);
        Assert.True(securityCommit.Succeeded);
        Assert.True(catalog.GetSnapshot().Generation > generationBeforeZoom);
        Assert.Equal(0, catalog.GetMemoryCacheStatistics().CurrentBytes);
    }

    [Fact]
    public async Task PrepareFailure_DoesNotReplaceActiveSnapshotOrPersistSettings()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        DictionaryCatalogSnapshot before = catalog.GetSnapshot();
        ApplicationSettings invalid = catalog.GetSettingsSnapshot() with
        {
            Dictionaries =
            [
                new DictionaryDefinition(
                    new DictionaryId("missing"),
                    "Missing",
                    null,
                    System.IO.Path.Combine(temporary.Path, "missing.mdx"),
                    true,
                    true,
                    [],
                    [],
                    DictionaryAppearance.Default)
            ]
        };

        SettingsPrepareResult preparation = await catalog.PrepareUpdateAsync(invalid);

        Assert.False(preparation.Succeeded);
        Assert.Equal(before, catalog.GetSnapshot());
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task DisabledMissingDictionary_DoesNotBlockSavingUnrelatedSettings()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            CompatibilityDiagnosticsEnabled = true,
            Dictionaries =
            [
                CreateDictionary(
                    "disabled-missing",
                    System.IO.Path.Combine(temporary.Path, "missing.mdx")) with
                {
                    Enabled = false
                }
            ]
        };

        SettingsPrepareResult preparation = await catalog.PrepareUpdateAsync(settings);
        SettingsCommitResult commit = preparation.Plan is null
            ? new SettingsCommitResult(false, 0, 0, "No plan")
            : await catalog.CommitUpdateAsync(preparation.Plan);

        Assert.True(preparation.Succeeded);
        Assert.True(commit.Succeeded);
        Assert.True(catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled);
        Assert.Empty(catalog.GetSnapshot().ActiveDictionaries);
    }

    [Fact]
    public async Task ZoomUpdate_PersistsRevisionWithoutAdvancingCatalogGeneration()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(
            paths,
            store,
            new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        List<SettingsCommittedEvent> events = [];
        catalog.SettingsCommitted += (_, value) => events.Add(value);
        long generation = catalog.GetSnapshot().Generation;
        long revision = catalog.GetSnapshot().SettingsRevision;

        SettingsCommitResult commit = await catalog.UpdateZoomAsync(
            1.35,
            revision,
            1.0);

        Assert.True(commit.Succeeded);
        Assert.Equal(generation, commit.CatalogGeneration);
        Assert.Equal(revision + 1, commit.Revision);
        Assert.Equal(1.35, catalog.GetSettingsSnapshot().Appearance.ContentZoom, 3);
        SettingsCommittedEvent committed = Assert.Single(events);
        Assert.True(committed.Changes.Has(SettingsChangeKind.Zoom));
        Assert.False(committed.Changes.RequiresCatalogReload);
        SettingsLoadResult persisted = await store.LoadAsync();
        Assert.Equal(1.35, persisted.Settings.Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task CandidatePaneWidthUpdate_PersistsRevisionWithoutAdvancingCatalogGeneration()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(
            paths,
            store,
            new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        List<SettingsCommittedEvent> events = [];
        catalog.SettingsCommitted += (_, value) => events.Add(value);
        long generation = catalog.GetSnapshot().Generation;
        long revision = catalog.GetSnapshot().SettingsRevision;

        SettingsCommitResult commit = await catalog.UpdateCandidatePaneWidthAsync(
            311.6,
            revision,
            null);

        Assert.True(commit.Succeeded);
        Assert.Equal(generation, commit.CatalogGeneration);
        Assert.Equal(revision + 1, commit.Revision);
        Assert.Equal(312, catalog.GetSettingsSnapshot().Appearance.CandidatePaneWidth);
        SettingsCommittedEvent committed = Assert.Single(events);
        Assert.True(committed.Changes.Has(SettingsChangeKind.MainWindowLayout));
        Assert.False(committed.Changes.RequiresCatalogReload);
        SettingsLoadResult persisted = await store.LoadAsync();
        Assert.Equal(312, persisted.Settings.Appearance.CandidatePaneWidth);
    }

    [Fact]
    public async Task DuplicateContent_IsRejectedBeforeCatalogChanges()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string firstPath = System.IO.Path.Combine(temporary.Path, "first.mdx");
        string secondPath = System.IO.Path.Combine(temporary.Path, "second.mdx");
        MDictFixture.Write(
            firstPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        File.Copy(firstPath, secondPath);
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Dictionaries =
            [
                CreateDictionary("first", firstPath),
                CreateDictionary("second", secondPath)
            ]
        };
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();

        SettingsPrepareResult result = await catalog.PrepareUpdateAsync(settings);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => error.Message.Contains(
                "Validation_MdxContentDuplicate",
                StringComparison.Ordinal));
        Assert.Empty(catalog.GetSnapshot().ActiveDictionaries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
    }

    [Fact]
    public async Task StalePlan_IsCleanedAndThrowingSubscriberDoesNotBreakCommittedState()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Dictionaries = [CreateDictionary("demo", mdxPath)]
        };
        AtomicSettingsStore store = new(paths);
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        SettingsPrepareResult first = await catalog.PrepareUpdateAsync(settings);
        SettingsPrepareResult second = await catalog.PrepareUpdateAsync(settings);
        string secondStagedPath = Assert.Single(second.Plan!.IndexChanges).StagedIndexPath!;
        catalog.SettingsCommitted += (_, _) => throw new InvalidOperationException("subscriber");

        SettingsCommitResult firstCommit = await catalog.CommitUpdateAsync(first.Plan!);
        SettingsCommitResult staleCommit = await catalog.CommitUpdateAsync(second.Plan!);

        Assert.True(firstCommit.Succeeded);
        Assert.False(staleCommit.Succeeded);
        Assert.Equal(1, catalog.GetSettingsSnapshot().Revision);
        Assert.False(File.Exists(secondStagedPath));
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(secondStagedPath)));
    }

    [Fact]
    public async Task RetiringStore_WaitsForOutstandingLease()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        DictionaryDefinition dictionary = CreateDictionary("demo", mdxPath);
        DictionaryIndexBuilder builder = new();
        StagedDictionaryIndex staged = await builder.PrepareAsync(
            dictionary,
            paths.TempDirectory);
        builder.Promote(staged, paths.IndexesDirectory);
        await using DictionaryStore store = await DictionaryStore.OpenAsync(
            dictionary,
            paths.IndexesDirectory,
            CancellationToken.None);
        DictionaryStore.StoreLease lease = store.AcquireLease();

        Task retirement = store.RetireAsync();
        await Task.Delay(30);
        Assert.False(retirement.IsCompleted);

        lease.Dispose();
        await retirement;
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => Task.Run(() => store.AcquireLease()));
    }

    [Fact]
    public async Task StoreReuse_RequiresUnchangedDefinitionAndSourceStamps()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        string cssPath = System.IO.Path.Combine(temporary.Path, "demo.css");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        File.WriteAllText(cssPath, "body{}");
        DictionaryDefinition dictionary = CreateDictionary("demo", mdxPath) with
        {
            CssPaths = [cssPath]
        };
        DictionaryIndexBuilder builder = new();
        StagedDictionaryIndex staged = await builder.PrepareAsync(
            dictionary,
            paths.TempDirectory);
        builder.Promote(staged, paths.IndexesDirectory);
        Assert.True(DictionaryIndexPath.TryGet(
            paths.IndexesDirectory,
            mdxPath,
            out string indexPath));
        await using DictionaryStore store = await DictionaryStore.OpenAsync(
            dictionary,
            paths.IndexesDirectory,
            CancellationToken.None);

        Assert.True(store.CanReuseFor(dictionary, indexPath));

        File.AppendAllText(cssPath, "p{}");

        Assert.False(store.CanReuseFor(dictionary, indexPath));
    }

    [Fact]
    public async Task CanceledPreparation_LeavesNoSnapshotOrCacheHalfProduct()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        ApplicationSettings settings = ApplicationSettings.Default with
        {
            Dictionaries = [CreateDictionary("demo", mdxPath)]
        };
        await using DictionaryCatalog catalog = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.PrepareUpdateAsync(settings, cancellation.Token));

        Assert.Empty(catalog.GetSnapshot().ActiveDictionaries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.IndexesDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.TempDirectory));
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task CrossProcessCommit_ReplaysDifferentFieldPatchAgainstLatestDiskRevision()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        await using DictionaryCatalog first = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await using DictionaryCatalog second = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await first.InitializeAsync();
        await second.InitializeAsync();
        SettingsPrepareResult prepared = await first.PrepareUpdateAsync(
            first.GetSettingsSnapshot() with { CompatibilityDiagnosticsEnabled = true });
        Assert.True(prepared.Succeeded);
        Assert.NotNull(prepared.Plan);

        SettingsCommitResult zoomCommit = await second.UpdateZoomAsync(1.2, 0, 1.0);
        SettingsCommitResult replayed = await first.CommitUpdateAsync(prepared.Plan!);

        Assert.True(zoomCommit.Succeeded);
        Assert.True(replayed.Succeeded);
        Assert.Equal(0, replayed.CatalogGeneration);
        Assert.True(first.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled);
        Assert.Equal(1.2, first.GetSettingsSnapshot().Appearance.ContentZoom, 3);
        SettingsLoadResult persisted = await new AtomicSettingsStore(paths).LoadAsync();
        Assert.Equal(2, persisted.Settings.Revision);
        Assert.True(persisted.Settings.CompatibilityDiagnosticsEnabled);
        Assert.Equal(1.2, persisted.Settings.Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task CrossProcessCommit_RejectsSameFieldConflictWithoutOverwritingDisk()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        await using DictionaryCatalog first = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await using DictionaryCatalog second = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await first.InitializeAsync();
        await second.InitializeAsync();
        SettingsPrepareResult prepared = await first.PrepareUpdateAsync(
            first.GetSettingsSnapshot() with
            {
                Appearance = first.GetSettingsSnapshot().Appearance with
                {
                    ContentZoom = 1.4
                }
            });
        Assert.True(prepared.Succeeded);
        Assert.NotNull(prepared.Plan);

        Assert.True((await second.UpdateZoomAsync(1.2, 0, 1.0)).Succeeded);
        SettingsCommitResult conflict = await first.CommitUpdateAsync(prepared.Plan!);

        Assert.False(conflict.Succeeded);
        Assert.Contains("Settings_Save_Conflict", conflict.Error);
        SettingsLoadResult persisted = await new AtomicSettingsStore(paths).LoadAsync();
        Assert.Equal(1, persisted.Settings.Revision);
        Assert.Equal(1.2, persisted.Settings.Appearance.ContentZoom, 3);
        Assert.Equal(1.0, first.GetSettingsSnapshot().Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task CrossProcessZoom_RepreparesExternalCatalogChangeBeforeCommit()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "external.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        await using DictionaryCatalog first = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await using DictionaryCatalog second = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        await first.InitializeAsync();
        await second.InitializeAsync();
        SettingsPrepareResult externalPreparation = await second.PrepareUpdateAsync(
            second.GetSettingsSnapshot() with
            {
                Dictionaries = [CreateDictionary("external", mdxPath)]
            });
        Assert.NotNull(externalPreparation.Plan);
        Assert.True((await second.CommitUpdateAsync(externalPreparation.Plan!)).Succeeded);

        SettingsCommitResult zoom = await first.UpdateZoomAsync(1.2, 0, 1.0);

        Assert.True(zoom.Succeeded);
        Assert.Equal(1, zoom.CatalogGeneration);
        Assert.Equal(1.2, first.GetSettingsSnapshot().Appearance.ContentZoom, 3);
        Assert.Single(first.GetSnapshot().ActiveDictionaries);
        Assert.Single(await first.LookupAsync(new LookupQuery("word", new DictionaryId("external"))));
        SettingsLoadResult persisted = await new AtomicSettingsStore(paths).LoadAsync();
        Assert.Equal(2, persisted.Settings.Revision);
        Assert.Equal(1.2, persisted.Settings.Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task CacheCleanup_DoesNotTouchFilesOutsideSelectedCategories()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string resource = System.IO.Path.Combine(paths.ResourcesDirectory, "resource.bin");
        string temporaryFile = System.IO.Path.Combine(paths.TempDirectory, "temporary.bin");
        string settings = System.IO.Path.Combine(paths.SettingsDirectory, "keep.json");
        string outside = System.IO.Path.Combine(paths.DataRoot, "keep.outside");
        await File.WriteAllTextAsync(resource, "resource");
        await File.WriteAllTextAsync(temporaryFile, "temporary");
        await File.WriteAllTextAsync(settings, "settings");
        await File.WriteAllTextAsync(outside, "outside");
        await using DictionaryCatalog catalog = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        CacheMaintenanceService maintenance = new(paths, catalog);

        CacheCleanupResult result = await maintenance.CleanAsync(
            CacheCategory.Resources | CacheCategory.Temp);

        Assert.Empty(result.Errors);
        Assert.False(File.Exists(resource));
        Assert.False(File.Exists(temporaryFile));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task WebView2Cleanup_RequiresProfileApiAndDoesNotDeleteLiveDirectory()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string marker = System.IO.Path.Combine(paths.WebView2Directory, "in-use.dat");
        await File.WriteAllTextAsync(marker, "active profile");
        await using DictionaryCatalog catalog = new(
            paths,
            new AtomicSettingsStore(paths),
            new DictionaryIndexBuilder());
        CacheMaintenanceService maintenance = new(paths, catalog);

        ImmutableArray<CacheCategoryUsage> usage = await maintenance.InspectAsync();
        CacheCleanupResult result = await maintenance.CleanAsync(CacheCategory.WebView2);

        CacheCategoryUsage recordBlocks = Assert.Single(
            usage.Where(item => item.Category == CacheCategory.MemoryRecordBlocks));
        Assert.Equal(DictionaryMemoryCache.RecordBlockCapacityBytes,
            recordBlocks.Runtime!.CapacityBytes);
        Assert.Contains(
            usage,
            item => item.Category == CacheCategory.MemoryDecodedResources &&
                    item.Runtime is not null);
        Assert.Contains(usage, item => item.Category == CacheCategory.NegativeResources);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Maintenance_WebViewProfileRequired"));
        Assert.True(File.Exists(marker));
        Assert.Equal(0, result.DeletedFiles);
    }

    [Fact]
    public async Task ResourceCacheCleanup_ReleasesStoresAndRestoresCatalogWithoutEagerRebuild()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        DictionaryDefinition dictionary = CreateDictionary("demo", mdxPath);
        AtomicSettingsStore store = new(paths);
        await store.SaveAsync(ApplicationSettings.Default with
        {
            Dictionaries = [dictionary]
        });
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        long generationBefore = catalog.GetSnapshot().Generation;
        int committedEvents = 0;
        catalog.SettingsCommitted += (_, _) => committedEvents++;
        string marker = System.IO.Path.Combine(paths.ResourcesDirectory, "cached-resource.bin");
        await File.WriteAllTextAsync(marker, "cached");
        CacheMaintenanceService maintenance = new(paths, catalog);

        CacheCleanupResult result = await maintenance.CleanAsync(CacheCategory.Resources);

        Assert.Empty(result.Errors);
        Assert.True(result.CatalogRefresh?.Succeeded);
        Assert.False(File.Exists(marker));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.ResourcesDirectory));
        Assert.True(catalog.GetSnapshot().Generation > generationBefore);
        Assert.Single(catalog.GetSnapshot().ActiveDictionaries);
        Assert.Equal(0, committedEvents);
        Assert.Single(await catalog.LookupAsync(new LookupQuery("word", dictionary.Id)));
    }

    [Fact]
    public async Task IndexRebuildPreparationFailure_PreservesLiveCatalog()
    {
        using TemporaryDirectory temporary = new();
        TestPaths paths = new(temporary.Path);
        paths.EnsureCreated();
        string mdxPath = System.IO.Path.Combine(temporary.Path, "demo.mdx");
        MDictFixture.Write(
            mdxPath,
            [("word", Encoding.UTF8.GetBytes("<p>entry</p>"))],
            utf16Keys: false);
        DictionaryDefinition dictionary = CreateDictionary("demo", mdxPath);
        AtomicSettingsStore store = new(paths);
        await store.SaveAsync(ApplicationSettings.Default with
        {
            Dictionaries = [dictionary]
        });
        await using DictionaryCatalog catalog = new(paths, store, new DictionaryIndexBuilder());
        await catalog.InitializeAsync();
        Directory.Delete(paths.TempDirectory, recursive: true);
        await File.WriteAllTextAsync(paths.TempDirectory, "blocks staging directory");

        SettingsCommitResult rebuild = await catalog.RebuildIndexesAsync();

        Assert.False(rebuild.Succeeded);
        Assert.Single(catalog.GetSnapshot().ActiveDictionaries);
        Assert.Single(await catalog.LookupAsync(new LookupQuery("word", dictionary.Id)));
    }

    [Fact]
    public void CachePathGuard_RejectsDirectoriesOutsideTheApprovedSet()
    {
        using TemporaryDirectory temporary = new();
        string approved = System.IO.Path.Combine(temporary.Path, "approved");
        string outside = System.IO.Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(approved);
        Directory.CreateDirectory(outside);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CachePathGuard.EnsureApproved(outside, [approved]));

        Assert.Contains("Maintenance_UnapprovedDirectory", exception.Message);
    }

    private static DictionaryDefinition CreateDictionary(string id, string mdxPath) => new(
        new DictionaryId(id),
        id,
        null,
        mdxPath,
        true,
        true,
        [],
        [],
        DictionaryAppearance.Default);

    private sealed class TestPaths(string root) : IAppDataPaths
    {
        public string DataRoot { get; } = root;
        public string SettingsDirectory { get; } = System.IO.Path.Combine(root, "Settings");
        public string SettingsFile => System.IO.Path.Combine(SettingsDirectory, "settings.json");
        public string SettingsBackupFile => System.IO.Path.Combine(SettingsDirectory, "settings.json.bak");
        public string IndexesDirectory { get; } = System.IO.Path.Combine(root, "Indexes");
        public string ResourcesDirectory { get; } = System.IO.Path.Combine(root, "Resources");
        public string WebView2Directory { get; } = System.IO.Path.Combine(root, "WebView2");
        public string LogsDirectory { get; } = System.IO.Path.Combine(root, "Logs");
        public string TempDirectory { get; } = System.IO.Path.Combine(root, "Temp");

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

    private static class MDictFixture
    {
        public static void Write(
            string path,
            ImmutableArray<(string Key, byte[] Record)> records,
            bool utf16Keys,
            string? title = null,
            int? recordBlockSplitOffset = null)
        {
            Encoding keyEncoding = utf16Keys ? Encoding.Unicode : Encoding.UTF8;
            byte[] delimiter = utf16Keys ? [0, 0] : [0];
            using MemoryStream keyData = new();
            using MemoryStream recordData = new();
            foreach ((string key, byte[] record) in records)
            {
                WriteBigEndian(keyData, checked((uint)recordData.Length));
                keyData.Write(keyEncoding.GetBytes(key));
                keyData.Write(delimiter);
                recordData.Write(record);
            }

            byte[] keyBlock = WrapUncompressed(keyData.ToArray());
            string first = records[0].Key;
            string last = records[^1].Key;
            using MemoryStream keyInfo = new();
            WriteBigEndian(keyInfo, checked((uint)records.Length));
            keyInfo.WriteByte(checked((byte)first.Length));
            keyInfo.Write(keyEncoding.GetBytes(first));
            keyInfo.WriteByte(checked((byte)last.Length));
            keyInfo.Write(keyEncoding.GetBytes(last));
            WriteBigEndian(keyInfo, checked((uint)keyBlock.Length));
            WriteBigEndian(keyInfo, checked((uint)keyData.Length));

            byte[] recordBytes = recordData.ToArray();
            byte[][] recordPayloads = recordBlockSplitOffset is > 0 &&
                                      recordBlockSplitOffset < recordBytes.Length
                ? [recordBytes[..recordBlockSplitOffset.Value],
                   recordBytes[recordBlockSplitOffset.Value..]]
                : [recordBytes];
            byte[][] recordBlocks = recordPayloads
                .Select(WrapUncompressed)
                .ToArray();
            string headerText =
                $"""<Dictionary GeneratedByEngineVersion="1.2" Encoding="{(utf16Keys ? "UTF-16" : "UTF-8")}"{(title is null ? "" : $" Title=\"{title}\"")} />""";
            byte[] header = Encoding.Unicode.GetBytes(headerText + "\0");
            using FileStream output = File.Create(path);
            WriteBigEndian(output, checked((uint)header.Length));
            output.Write(header);
            WriteBigEndian(output, 0);
            WriteBigEndian(output, 1);
            WriteBigEndian(output, checked((uint)records.Length));
            WriteBigEndian(output, checked((uint)keyInfo.Length));
            WriteBigEndian(output, checked((uint)keyBlock.Length));
            keyInfo.Position = 0;
            keyInfo.CopyTo(output);
            output.Write(keyBlock);
            WriteBigEndian(output, checked((uint)recordBlocks.Length));
            WriteBigEndian(output, checked((uint)records.Length));
            WriteBigEndian(output, checked((uint)(recordBlocks.Length * 8)));
            WriteBigEndian(output, checked((uint)recordBlocks.Sum(block => block.Length)));
            for (int index = 0; index < recordBlocks.Length; index++)
            {
                WriteBigEndian(output, checked((uint)recordBlocks[index].Length));
                WriteBigEndian(output, checked((uint)recordPayloads[index].Length));
            }
            foreach (byte[] recordBlock in recordBlocks)
            {
                output.Write(recordBlock);
            }
        }

        private static byte[] WrapUncompressed(byte[] bytes)
        {
            byte[] output = new byte[8 + bytes.Length];
            bytes.CopyTo(output, 8);
            return output;
        }

        private static void WriteBigEndian(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
