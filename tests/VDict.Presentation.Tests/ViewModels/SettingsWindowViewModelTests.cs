using VDict.Core.Models;
using VDict.Presentation.Services;
using VDict.Presentation.Settings;
using VDict.Presentation.Tests.TestDoubles;
using VDict.Presentation.ViewModels;

namespace VDict.Presentation.Tests.ViewModels;

public sealed class SettingsWindowViewModelTests
{
    [Fact]
    public void MainWindow_BlocksSettingsUntilCatalogIsReady()
    {
        FakeModalWindowService modal = new();
        using ReaderViewModel reader = new(new FakeCatalog(), new ImmediateUiDispatcher());
        MainWindowViewModel viewModel = new(reader, modal);

        Assert.False(viewModel.OpenSettingsCommand.CanExecute(null));

        reader.MarkCatalogReady();

        Assert.True(viewModel.OpenSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void SavingState_DisablesEditingAndMaintenanceButKeepsCancellationAvailable()
    {
        SettingsWindowViewModel viewModel = new(
            new SettingsEditSession(ApplicationSettings.Default, new FakeCatalog()),
            new FakeDialogService());
        viewModel.BeginOpen();
        viewModel.MarkOpen();

        Assert.True(viewModel.TryBeginMaintenance());

        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanStartMaintenance);
        Assert.True(viewModel.CanCancelOperation);
    }

    [Fact]
    public async Task DirtyClose_FirstCancelsThenAllowsSecondCloseAfterDiscard()
    {
        FakeDialogService dialogs = new() { Choice = DialogChoice.Primary };
        SettingsEditSession session = new(ApplicationSettings.Default, new FakeCatalog());
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);
        SettingsWindowViewModel viewModel = new(session, dialogs);
        viewModel.BeginOpen();
        viewModel.MarkOpen();

        bool allow = await viewModel.CanCloseAsync();

        Assert.True(allow);
        Assert.True(viewModel.AllowClose);
        Assert.False(session.IsDirty);
        Assert.Equal(SettingsWindowState.Closing, viewModel.State);
        Assert.True(await viewModel.CanCloseAsync());
        Assert.Equal(1, dialogs.ShowCount);
    }

    [Fact]
    public async Task DirtyClose_ReturnsToOpenWhenUserContinuesEditing()
    {
        FakeDialogService dialogs = new() { Choice = DialogChoice.Cancel };
        SettingsEditSession session = new(ApplicationSettings.Default, new FakeCatalog());
        session.Update(
            settings => settings with { CompatibilityDiagnosticsEnabled = true },
            SettingsDirtySection.General);
        SettingsWindowViewModel viewModel = new(session, dialogs);
        viewModel.BeginOpen();
        viewModel.MarkOpen();

        Assert.False(await viewModel.CanCloseAsync());
        Assert.Equal(SettingsWindowState.Open, viewModel.State);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Maintenance_BlocksCloseUntilOperationEnds()
    {
        SettingsWindowViewModel viewModel = new(
            new SettingsEditSession(ApplicationSettings.Default, new FakeCatalog()),
            new FakeDialogService());
        viewModel.BeginOpen();
        viewModel.MarkOpen();

        Assert.True(viewModel.TryBeginMaintenance());
        Assert.Equal(SettingsWindowState.Saving, viewModel.State);
        Assert.False(await viewModel.CanCloseAsync());

        viewModel.EndMaintenance();
        Assert.Equal(SettingsWindowState.Open, viewModel.State);
        Assert.True(await viewModel.CanCloseAsync());
    }

    private sealed class FakeModalWindowService : IModalWindowService
    {
        public Task ShowSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
