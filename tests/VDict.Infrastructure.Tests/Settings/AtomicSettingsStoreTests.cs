using System.Collections.Immutable;
using System.Text.Json;
using VDict.Core.Environment;
using VDict.Core.Models;
using VDict.Infrastructure.Settings;

namespace VDict.Infrastructure.Tests.Settings;

public sealed class AtomicSettingsStoreTests
{
    [Fact]
    public async Task LockedSave_RejectsStaleDiskRevisionWithoutOverwriting()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        ApplicationSettings current = ApplicationSettings.Default with
        {
            Revision = 1,
            CompatibilityDiagnosticsEnabled = true
        };
        await store.SaveAsync(current);
        ApplicationSettings stale = ApplicationSettings.Default with
        {
            Revision = 1,
            Appearance = ApplicationAppearance.Default with { ContentZoom = 1.4 }
        };

        SettingsLockedSaveResult result = await store.TrySaveAsync(
            stale,
            expectedRevision: 0);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.CurrentSettings.Revision);
        SettingsLoadResult reloaded = await store.LoadAsync();
        Assert.True(reloaded.Settings.CompatibilityDiagnosticsEnabled);
        Assert.Equal(1.0, reloaded.Settings.Appearance.ContentZoom, 3);
    }

    [Fact]
    public async Task LockedSave_SerializesCompetingWritersWithDiskCas()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        AtomicSettingsStore first = new(paths);
        AtomicSettingsStore second = new(paths);
        await first.SaveAsync(ApplicationSettings.Default with { Revision = 1 });
        ApplicationSettings one = ApplicationSettings.Default with
        {
            Revision = 2,
            CompatibilityDiagnosticsEnabled = true
        };
        ApplicationSettings two = ApplicationSettings.Default with
        {
            Revision = 2,
            ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
        };

        SettingsLockedSaveResult[] results = await Task.WhenAll(
            first.TrySaveAsync(one, 1),
            second.TrySaveAsync(two, 1));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => !result.Succeeded);
        Assert.Equal(2, (await first.LoadAsync()).Settings.Revision);
    }

    [Fact]
    public async Task Save_WritesSchemaRevisionAndBackup()
    {
        using TemporaryPaths paths = new();
        AtomicSettingsStore store = new(paths);
        ApplicationSettings first = CreateSettings(1, "first");
        ApplicationSettings second = CreateSettings(2, "second");

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        using JsonDocument current = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        using JsonDocument backup = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsBackupFile));
        Assert.Equal(SettingsSchema.CurrentVersion, current.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(2, current.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(1, backup.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task SaveAfterV4Migration_PreservesPreMigrationBackup()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "schema_version": 4,
              "revision": 7,
              "dictionaries": [
                {
                  "id": "dict",
                  "name": "Dictionary",
                  "mdx_path": "C:\\dict\\sample.mdx",
                  "dark_mode_strategy": "original"
                }
              ]
            }
            """);
        AtomicSettingsStore store = new(paths);

        SettingsLoadResult loaded = await store.LoadAsync();
        await store.SaveAsync(loaded.Settings);

        using JsonDocument current = JsonDocument.Parse(
            await File.ReadAllTextAsync(paths.SettingsFile));
        using JsonDocument backup = JsonDocument.Parse(
            await File.ReadAllTextAsync(paths.SettingsBackupFile));
        Assert.Equal(
            SettingsSchema.CurrentVersion,
            current.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(4, backup.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(
            "original",
            backup.RootElement.GetProperty("dictionaries")[0]
                .GetProperty("dark_mode_strategy")
                .GetString());
    }

    [Fact]
    public async Task SaveAfterV5Migration_PreservesUnsetWidthBackup()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "schema_version": 5,
              "revision": 8,
              "dictionaries": [],
              "style": {
                "content_zoom": 1.0,
                "content_width_percent": 80
              }
            }
            """);
        AtomicSettingsStore store = new(paths);

        SettingsLoadResult loaded = await store.LoadAsync();
        await store.SaveAsync(loaded.Settings);

        using JsonDocument current = JsonDocument.Parse(
            await File.ReadAllTextAsync(paths.SettingsFile));
        using JsonDocument backup = JsonDocument.Parse(
            await File.ReadAllTextAsync(paths.SettingsBackupFile));
        Assert.Equal(
            SettingsSchema.CurrentVersion,
            current.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(5, backup.RootElement.GetProperty("schema_version").GetInt32());
        Assert.False(
            backup.RootElement.GetProperty("style")
                .TryGetProperty("candidate_pane_width", out _));
    }

    [Fact]
    public async Task Load_RecoversBackupAndPreservesCorruptPrimary()
    {
        using TemporaryPaths paths = new();
        AtomicSettingsStore store = new(paths);
        await store.SaveAsync(CreateSettings(1, "first"));
        await store.SaveAsync(CreateSettings(2, "second"));
        await File.WriteAllTextAsync(paths.SettingsFile, "{broken");

        SettingsLoadResult result = await store.LoadAsync();

        Assert.Equal(1, result.Settings.Revision);
        Assert.NotNull(result.RecoveryMessage);
        Assert.NotNull(result.PreservedCorruptPath);
        Assert.True(File.Exists(result.PreservedCorruptPath));
        Assert.Equal("{broken", await File.ReadAllTextAsync(paths.SettingsFile));
    }

    [Fact]
    public async Task SaveAfterBackupRecovery_DoesNotReplaceGoodBackupWithCorruptPrimary()
    {
        using TemporaryPaths paths = new();
        AtomicSettingsStore store = new(paths);
        await store.SaveAsync(CreateSettings(1, "first"));
        await store.SaveAsync(CreateSettings(2, "second"));
        await File.WriteAllTextAsync(paths.SettingsFile, "{broken");
        SettingsLoadResult recovered = await store.LoadAsync();

        await store.SaveAsync(recovered.Settings with
        {
            Revision = 3,
            CompatibilityDiagnosticsEnabled = true
        });
        await File.WriteAllTextAsync(paths.SettingsFile, "{broken-again");
        SettingsLoadResult recoveredAgain = await store.LoadAsync();

        Assert.Equal(1, recoveredAgain.Settings.Revision);
        Assert.Equal("first", Assert.Single(recoveredAgain.Settings.Dictionaries).Name);
    }

    [Fact]
    public async Task Import_RejectsInvalidDictionaryPathSyntax()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        AtomicSettingsStore store = new(paths);
        string source = Path.Combine(paths.DataRoot, "invalid-path.json");
        await store.ExportAsync(
            source,
            CreateSettings(1, "invalid") with
            {
                Dictionaries =
                [
                    CreateSettings(1, "invalid").Dictionaries[0] with
                    {
                        MdxPath = "invalid\0path.mdx"
                    }
                ]
            });

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ImportAsync(source));

        Assert.Contains("Validation_PathSyntax", exception.Message);
    }

    [Fact]
    public async Task Load_MigratesSchemaV0DocumentWithoutDeletingIt()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """
            {
              "dictionaries": [],
              "style": { "font_size": 20, "line_height": 1.7, "content_zoom": 1.2, "theme": "dark" },
              "external_link_policy": "system-browser"
            }
            """);
        AtomicSettingsStore store = new(paths);

        SettingsLoadResult result = await store.LoadAsync();

        Assert.Equal(0, result.Migration.SourceVersion);
        Assert.Equal(SettingsSchema.CurrentVersion, result.Migration.TargetVersion);
        Assert.Contains("schema-v0-to-v1", result.Migration.Steps);
        Assert.Contains("v1-local-script-mode-standard", result.Migration.Steps);
        Assert.Equal(ApplicationTheme.Dark, result.Settings.Appearance.Theme);
        Assert.Equal(ExternalLinkPolicy.SystemBrowser, result.Settings.ExternalLinkPolicy);
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void SchemaV1_MigratesMissingLocalScriptModeToStandardAndRoundTripsLegacy()
    {
        SettingsDocument v1 = new()
        {
            SchemaVersion = 1,
            Dictionaries =
            [
                new DictionaryDocument
                {
                    Id = "dict",
                    Name = "Dictionary",
                    MdxPath = @"C:\dict\sample.mdx"
                }
            ]
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v1);
        DictionaryDefinition standard = Assert.Single(migration.Settings.Dictionaries);
        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Equal(LocalScriptMode.Standard, standard.LocalScriptMode);

        ApplicationSettings legacySettings = migration.Settings with
        {
            Dictionaries = [standard with { LocalScriptMode = LocalScriptMode.Legacy }]
        };
        SettingsDocument serialized = SettingsSchema.FromModel(legacySettings);
        Assert.Equal("legacy", Assert.Single(serialized.Dictionaries).LocalScriptMode);
        Assert.Equal(
            LocalScriptMode.Legacy,
            Assert.Single(SettingsSchema.ToModel(serialized).Dictionaries).LocalScriptMode);
    }

    [Fact]
    public void StyleSchema_ReadsAndRoundTripsLegacyAppFontAndContentWidth()
    {
        SettingsDocument document = JsonSerializer.Deserialize<SettingsDocument>(
            """
            {
              "schema_version": 2,
              "style": {
                "app_font_family": "Legacy UI Font",
                "content_width_percent": 65
              }
            }
            """)!;

        ApplicationSettings settings = SettingsSchema.ToModel(document);
        SettingsDocument roundTrip = SettingsSchema.FromModel(settings);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(roundTrip));
        JsonElement style = json.RootElement.GetProperty("style");

        Assert.Equal("Legacy UI Font", settings.Appearance.AppFontFamily);
        Assert.Equal("Legacy UI Font", roundTrip.Style.AppFontFamily);
        Assert.Equal(65, settings.Appearance.ContentWidthPercent);
        Assert.Equal(65, style.GetProperty("content_width_percent").GetInt32());
    }

    [Fact]
    public void SchemaV2_MigratesMissingContentWidthToEightyPercent()
    {
        SettingsDocument v2 = new()
        {
            SchemaVersion = 2,
            Style = new StyleDocument { ContentWidthPercent = 0 }
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v2);

        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Contains("v2-content-width-percent-default-80", migration.Steps);
        Assert.Equal(80, migration.Settings.Appearance.ContentWidthPercent);
    }

    [Fact]
    public void SchemaV3_MigratesToNativeRenderingDefaultsAndRoundTripsScripts()
    {
        SettingsDocument v3 = new()
        {
            SchemaVersion = 3,
            Dictionaries =
            [
                new DictionaryDocument
                {
                    Id = "dict",
                    Name = "Dictionary",
                    MdxPath = @"C:\dict\sample.mdx",
                    DarkModeStrategy = "global",
                    OverrideTypography = true,
                    ScriptPaths = [@"C:\dict\dependency.js"]
                },
                new DictionaryDocument
                {
                    Id = "dark",
                    Name = "Dark",
                    MdxPath = @"C:\dict\dark.mdx",
                    DarkModeStrategy = "dark"
                }
            ]
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v3);
        DictionaryDefinition dictionary = migration.Settings.Dictionaries[0];
        DictionaryDefinition dark = migration.Settings.Dictionaries[1];
        SettingsDocument serialized = SettingsSchema.FromModel(migration.Settings);

        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Contains("v3-native-dictionary-rendering-defaults", migration.Steps);
        Assert.Contains("v4-preserve-dictionary-theme-intent", migration.Steps);
        Assert.False(dictionary.Appearance.OverrideTypography);
        Assert.Equal(DictionaryDarkModeStrategy.Global, dictionary.Appearance.DarkModeStrategy);
        Assert.Equal(DictionaryDarkModeStrategy.Dark, dark.Appearance.DarkModeStrategy);
        Assert.Equal(@"C:\dict\dependency.js", Assert.Single(dictionary.ScriptPaths));
        Assert.Equal("global", serialized.Dictionaries[0].DarkModeStrategy);
        Assert.Equal(@"C:\dict\dependency.js", Assert.Single(serialized.Dictionaries[0].ScriptPaths));
    }

    [Fact]
    public void SchemaV4_PreservesAmbiguousOriginalWithoutGuessingUserIntent()
    {
        SettingsDocument v4 = new()
        {
            SchemaVersion = 4,
            Dictionaries =
            [
                new DictionaryDocument
                {
                    Id = "dict",
                    Name = "Dictionary",
                    MdxPath = @"C:\dict\sample.mdx",
                    DarkModeStrategy = "original"
                }
            ]
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v4);

        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Contains("v4-preserve-dictionary-theme-intent", migration.Steps);
        Assert.Equal(
            DictionaryDarkModeStrategy.Light,
            Assert.Single(migration.Settings.Dictionaries).Appearance.DarkModeStrategy);
    }

    [Fact]
    public void SchemaV5_MigratesCandidatePaneWidthAsUnsetAndRoundTripsExplicitWidth()
    {
        SettingsDocument v5 = new()
        {
            SchemaVersion = 5,
            Style = new StyleDocument()
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v5);

        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Contains("v5-candidate-pane-width-unset", migration.Steps);
        Assert.Null(migration.Settings.Appearance.CandidatePaneWidth);

        ApplicationSettings adjusted = migration.Settings with
        {
            Appearance = migration.Settings.Appearance with { CandidatePaneWidth = 296 }
        };
        SettingsDocument serialized = SettingsSchema.FromModel(adjusted);
        ApplicationSettings roundTrip = SettingsSchema.ToModel(serialized);

        Assert.Equal(296, serialized.Style.CandidatePaneWidth);
        Assert.Equal(296, roundTrip.Appearance.CandidatePaneWidth);
    }

    [Fact]
    public void SchemaV6_MigratesDiagnosticsToStandardAndRoundTripsDetailedAndStrict()
    {
        SettingsDocument v6 = new()
        {
            SchemaVersion = 6,
            CompatibilityDiagnosticsEnabled = true,
            Dictionaries =
            [
                new DictionaryDocument
                {
                    Id = "dict",
                    Name = "Dictionary",
                    MdxPath = "dictionary.mdx"
                }
            ]
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v6);

        Assert.Contains("v6-diagnostics-level-standard", migration.Steps);
        Assert.Equal(
            CompatibilityDiagnosticsLevel.Standard,
            migration.Settings.DiagnosticsLevel);

        ApplicationSettings detailed = migration.Settings with
        {
            DetailedDiagnosticsEnabled = true,
            Dictionaries =
            [
                Assert.Single(migration.Settings.Dictionaries) with
                {
                    LocalScriptMode = LocalScriptMode.Strict
                }
            ]
        };
        SettingsDocument serialized = SettingsSchema.FromModel(detailed);
        ApplicationSettings roundTrip = SettingsSchema.ToModel(serialized);

        Assert.True(serialized.CompatibilityDiagnosticsDetailed);
        Assert.Equal("strict", Assert.Single(serialized.Dictionaries).LocalScriptMode);
        Assert.Equal(CompatibilityDiagnosticsLevel.Detailed, roundTrip.DiagnosticsLevel);
        Assert.Equal(
            LocalScriptMode.Strict,
            Assert.Single(roundTrip.Dictionaries).LocalScriptMode);
    }

    [Fact]
    public void SchemaV7_MigratesLanguageToSystemAndRoundTripsAllChoices()
    {
        SettingsDocument v7 = new()
        {
            SchemaVersion = 7
        };

        SettingsMigrationResult migration = SettingsSchema.Migrate(v7);

        Assert.Equal(SettingsSchema.CurrentVersion, migration.TargetVersion);
        Assert.Contains("v7-ui-language-system", migration.Steps);
        Assert.Equal(ApplicationLanguage.System, migration.Settings.UiLanguage);

        foreach ((ApplicationLanguage language, string persisted) in new[]
                 {
                     (ApplicationLanguage.System, "system"),
                     (ApplicationLanguage.ChineseSimplified, "zh-CN"),
                     (ApplicationLanguage.English, "en-US")
                 })
        {
            SettingsDocument serialized = SettingsSchema.FromModel(
                migration.Settings with { UiLanguage = language });
            ApplicationSettings roundTrip = SettingsSchema.ToModel(serialized);

            Assert.Equal(persisted, serialized.UiLanguage);
            Assert.Equal(language, roundTrip.UiLanguage);
        }
    }

    [Fact]
    public void StartupLanguageReader_UsesPrimaryBackupAndSafeSystemFallback()
    {
        using TemporaryPaths paths = new();
        paths.EnsureCreated();

        Assert.Equal(ApplicationLanguage.System, StartupLanguageReader.Read(paths));

        File.WriteAllText(paths.SettingsBackupFile, """{"ui_language":"en-US"}""");
        File.WriteAllText(paths.SettingsFile, "{broken");
        Assert.Equal(ApplicationLanguage.English, StartupLanguageReader.Read(paths));

        File.WriteAllText(paths.SettingsFile, """{"ui_language":"zh-CN"}""");
        Assert.Equal(
            ApplicationLanguage.ChineseSimplified,
            StartupLanguageReader.Read(paths));

        File.WriteAllText(paths.SettingsFile, """{"ui_language":"unsupported"}""");
        Assert.Equal(ApplicationLanguage.System, StartupLanguageReader.Read(paths));
    }

    private static ApplicationSettings CreateSettings(long revision, string name)
    {
        DictionaryDefinition dictionary = new(
            new DictionaryId("dict"),
            name,
            null,
            "C:\\dict\\sample.mdx",
            true,
            true,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            DictionaryAppearance.Default);
        return ApplicationSettings.Default with
        {
            Revision = revision,
            Dictionaries = [dictionary]
        };
    }

    private sealed class TemporaryPaths : IAppDataPaths, IDisposable
    {
        public TemporaryPaths()
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "VDict.Tests", Guid.NewGuid().ToString("N"));
            SettingsDirectory = Path.Combine(DataRoot, "Settings");
            SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
            SettingsBackupFile = Path.Combine(SettingsDirectory, "settings.json.bak");
            IndexesDirectory = Path.Combine(DataRoot, "Cache", "Indexes");
            ResourcesDirectory = Path.Combine(DataRoot, "Cache", "Resources");
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
            Directory.CreateDirectory(SettingsDirectory);
            Directory.CreateDirectory(IndexesDirectory);
            Directory.CreateDirectory(ResourcesDirectory);
            Directory.CreateDirectory(WebView2Directory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(TempDirectory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
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
