using VDict.Core.Models;
using VDict.Presentation.Settings;
using VDict.Presentation.Tests.TestDoubles;

namespace VDict.Presentation.Tests.Settings;

public sealed class SettingsEditSessionTests
{
    [Fact]
    public void AddRemoveAndMove_UseStructuredDirtyState()
    {
        FakeCatalog catalog = new();
        DictionaryDefinition first = Dictionary("first");
        DictionaryDefinition second = Dictionary("second");
        SettingsEditSession session = new(
            ApplicationSettings.Default with { Dictionaries = [first, second] },
            catalog);

        Assert.True(session.MoveDictionary(first.Id, 1));
        Assert.Equal("second", session.Draft.Dictionaries[0].Id.Value);
        Assert.True(session.IsDirty);
        Assert.True(session.DirtySections.HasFlag(SettingsDirtySection.Dictionaries));

        Assert.True(session.RemoveDictionary(first.Id));
        session.AddDictionary(first);
        Assert.Equal(2, session.Draft.Dictionaries.Length);
    }

    [Fact]
    public void Cancel_RestoresOriginalSnapshot()
    {
        FakeCatalog catalog = new();
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);

        session.Cancel();

        Assert.False(session.IsDirty);
        Assert.False(session.Draft.CompatibilityDiagnosticsEnabled);
    }

    [Fact]
    public void RevertingOneSection_ClearsOnlyThatStructuredDirtyFlag()
    {
        FakeCatalog catalog = new();
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);
        session.Update(
            settings => settings with
            {
                ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
            },
            SettingsDirtySection.SecurityAndNetwork);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = false },
            SettingsDirtySection.General);

        Assert.False(session.DirtySections.HasFlag(SettingsDirtySection.General));
        Assert.True(session.DirtySections.HasFlag(SettingsDirtySection.SecurityAndNetwork));
    }

    [Fact]
    public void ExistingDictionarySourceFiles_AreImmutableWhileOtherFieldsRemainEditable()
    {
        DictionaryDefinition original = Dictionary("dict") with
        {
            MddPaths = [@"C:\old.mdd"],
            CssPaths = [@"C:\old.css"],
            ScriptPaths = [@"C:\old.js"]
        };
        SettingsEditSession session = new(
            ApplicationSettings.Default with { Dictionaries = [original] },
            new FakeCatalog());
        DictionaryDefinition edited = original with
        {
            DisplayName = "Custom name",
            MdxPath = @"D:\new.mdx",
            MddPaths = [@"D:\new.mdd"],
            CssPaths = [@"D:\new.css"],
            ScriptPaths = [@"D:\new.js"]
        };

        Assert.True(session.ReplaceDictionary(edited));

        DictionaryDefinition actual = Assert.Single(session.Draft.Dictionaries);
        Assert.Equal("Custom name", actual.DisplayName);
        Assert.Equal(original.MdxPath, actual.MdxPath);
        Assert.Equal(original.MddPaths, actual.MddPaths);
        Assert.Equal(original.CssPaths, actual.CssPaths);
        Assert.Equal(original.ScriptPaths, actual.ScriptPaths);

        session.Update(
            settings => settings with
            {
                Dictionaries =
                [
                    actual with
                    {
                        DisplayName = "Imported name",
                        MdxPath = @"E:\imported.mdx",
                        MddPaths = [@"E:\imported.mdd"],
                        CssPaths = [@"E:\imported.css"],
                        ScriptPaths = [@"E:\imported.js"]
                    }
                ]
            },
            SettingsDirtySection.Dictionaries);

        actual = Assert.Single(session.Draft.Dictionaries);
        Assert.Equal("Imported name", actual.DisplayName);
        Assert.Equal(original.MdxPath, actual.MdxPath);
        Assert.Equal(original.MddPaths, actual.MddPaths);
        Assert.Equal(original.CssPaths, actual.CssPaths);
        Assert.Equal(original.ScriptPaths, actual.ScriptPaths);
    }

    [Fact]
    public void CandidatePaneWidth_ContributesToStructuredDirtyState()
    {
        SettingsEditSession session = new(ApplicationSettings.Default, new FakeCatalog());

        session.Update(
            settings => settings with
            {
                Appearance = settings.Appearance with { CandidatePaneWidth = 300 }
            },
            SettingsDirtySection.Appearance);

        Assert.True(session.IsDirty);
        Assert.True(session.DirtySections.HasFlag(SettingsDirtySection.Appearance));
    }

    [Fact]
    public async Task SaveFailure_PreservesDraftAndDirtyState()
    {
        FakeCatalog catalog = new()
        {
            PrepareHandler = (_, _) => Task.FromResult(
                new SettingsPrepareResult(
                    null,
                    [new DictionaryPreparationError(null, "prepare failed")]))
        };
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);

        SettingsCommitResult result = await session.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.True(session.IsDirty);
        Assert.True(session.Draft.CompatibilityDiagnosticsEnabled);
        Assert.Equal("prepare failed", session.SaveError);
    }

    [Fact]
    public async Task SuccessfulSave_AdoptsCommittedSnapshotAndClearsDirtyState()
    {
        FakeCatalog catalog = new();
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);

        SettingsCommitResult result = await session.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.False(session.IsDirty);
        Assert.Equal(1, session.Original.Revision);
        Assert.True(session.Original.CompatibilityDiagnosticsEnabled);
    }

    [Fact]
    public async Task SaveWithoutChanges_DoesNotCreateARevision()
    {
        FakeCatalog catalog = new() { Revision = 4, Generation = 2 };
        SettingsEditSession session = new(
            catalog.GetSettingsSnapshot(),
            catalog);

        SettingsCommitResult result = await session.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Revision);
        Assert.Equal(2, result.CatalogGeneration);
        Assert.Equal(4, catalog.Revision);
    }

    [Fact]
    public async Task Save_ReportsPreparationAndCommitProgress()
    {
        FakeCatalog catalog = new();
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);
        List<SettingsOperationProgress> reports = [];

        SettingsCommitResult result = await session.SaveAsync(
            progress: new ImmediateProgress<SettingsOperationProgress>(reports.Add));

        Assert.True(result.Succeeded);
        Assert.Contains(reports, report => report.Phase == "prepare");
        Assert.Contains(reports, report => report.Phase == "commit");
        Assert.Contains(reports, report => report.Phase == "complete");
    }

    [Fact]
    public async Task SuccessfulSave_DoesNotOverwriteEditsMadeWhileSaveWasInFlight()
    {
        TaskCompletionSource<SettingsPrepareResult> preparation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCatalog catalog = new()
        {
            PrepareHandler = async (settings, _) =>
            {
                SettingsPrepareResult result = await preparation.Task;
                return result with
                {
                    Plan = result.Plan! with
                    {
                        Settings = settings with { Revision = 1 }
                    }
                };
            }
        };
        SettingsEditSession session = new(ApplicationSettings.Default, catalog);
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);

        Task<SettingsCommitResult> save = session.SaveAsync();
        await WaitUntilAsync(() => session.IsSaving);
        session.Update(
            settings => settings with
            {
                ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
            },
            SettingsDirtySection.SecurityAndNetwork);
        preparation.SetResult(new SettingsPrepareResult(
            new SettingsCommitPlan(ApplicationSettings.Default, 0, [], new object()),
            []));

        SettingsCommitResult result = await save;

        Assert.True(result.Succeeded);
        Assert.True(session.Original.CompatibilityDiagnosticsEnabled);
        Assert.Equal(ExternalLinkPolicy.Block, session.Original.ExternalLinkPolicy);
        Assert.Equal(ExternalLinkPolicy.SystemBrowser, session.Draft.ExternalLinkPolicy);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void RestoreActions_AreSeparatedByScope()
    {
        DictionaryDefinition dictionary = Dictionary("dict") with
        {
            Appearance = DictionaryAppearance.Default with { FontSize = 30 }
        };
        ApplicationSettings customized = ApplicationSettings.Default with
        {
            Dictionaries = [dictionary],
            Appearance = ApplicationAppearance.Default with
            {
                Theme = ApplicationTheme.Dark,
                FontSize = 28,
                ContentWidthPercent = 55
            },
            ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
        };
        SettingsEditSession session = new(customized, new FakeCatalog());

        session.RestoreReaderDefaults();
        Assert.Equal(ApplicationTheme.Dark, session.Draft.Appearance.Theme);
        Assert.Equal(ApplicationAppearance.Default.FontSize, session.Draft.Appearance.FontSize);
        Assert.Equal(
            ApplicationAppearance.Default.ContentWidthPercent,
            session.Draft.Appearance.ContentWidthPercent);
        Assert.Equal(30, session.Draft.Dictionaries[0].Appearance.FontSize);

        session.RestoreDictionaryAppearance(dictionary.Id);
        Assert.Equal(DictionaryAppearance.Default, session.Draft.Dictionaries[0].Appearance);
        Assert.Equal(ExternalLinkPolicy.SystemBrowser, session.Draft.ExternalLinkPolicy);

        session.RestoreAllDefaults();
        Assert.Equal(ApplicationAppearance.Default, session.Draft.Appearance);
        Assert.Equal(ExternalLinkPolicy.Block, session.Draft.ExternalLinkPolicy);
        Assert.Single(session.Draft.Dictionaries);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static DictionaryDefinition Dictionary(string id) => new(
        new DictionaryId(id),
        id,
        null,
        $"C:\\{id}.mdx",
        true,
        true,
        [],
        [],
        DictionaryAppearance.Default);

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
