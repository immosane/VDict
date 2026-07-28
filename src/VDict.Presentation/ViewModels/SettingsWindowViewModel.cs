using CommunityToolkit.Mvvm.ComponentModel;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Presentation.Services;
using VDict.Presentation.Settings;

namespace VDict.Presentation.ViewModels;

public enum SettingsWindowState
{
    Closed,
    Opening,
    Open,
    Saving,
    Closing
}

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IAppLocalizer _localizer;

    [ObservableProperty]
    private SettingsWindowState _state = SettingsWindowState.Closed;

    [ObservableProperty]
    private bool _allowClose;

    [ObservableProperty]
    private SettingsOperationProgress? _operationProgress;

    public SettingsWindowViewModel(
        SettingsEditSession session,
        IDialogService dialogs,
        IAppLocalizer? localizer = null)
    {
        Session = session;
        _dialogs = dialogs;
        _localizer = localizer ?? IdentityAppLocalizer.Instance;
        General = new GeneralSettingsViewModel(session);
        Dictionaries = new DictionaryLibrarySettingsViewModel(session);
        Appearance = new AppearanceSettingsViewModel(session);
        Reader = new ReaderSettingsViewModel(session);
        Security = new SecuritySettingsViewModel(session);
        Storage = new StorageSettingsViewModel(session);
        About = new AboutViewModel();
    }

    public SettingsEditSession Session { get; }

    public GeneralSettingsViewModel General { get; }

    public DictionaryLibrarySettingsViewModel Dictionaries { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    public ReaderSettingsViewModel Reader { get; }

    public SecuritySettingsViewModel Security { get; }

    public StorageSettingsViewModel Storage { get; }

    public AboutViewModel About { get; }

    public bool CanEdit => State == SettingsWindowState.Open;

    public bool CanStartMaintenance => State == SettingsWindowState.Open;

    public bool CanCancelOperation => State == SettingsWindowState.Saving;

    partial void OnStateChanged(SettingsWindowState value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanStartMaintenance));
        OnPropertyChanged(nameof(CanCancelOperation));
    }

    public void BeginOpen()
    {
        if (State == SettingsWindowState.Closed)
        {
            State = SettingsWindowState.Opening;
        }
    }

    public void MarkOpen()
    {
        if (State == SettingsWindowState.Opening)
        {
            State = SettingsWindowState.Open;
        }
    }

    public async Task<SettingsCommitResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (State is SettingsWindowState.Saving or SettingsWindowState.Closing)
        {
            return new SettingsCommitResult(
                false,
                Session.Original.Revision,
                0,
                _localizer.Get("Settings_Save_WindowUnavailable"));
        }

        State = SettingsWindowState.Saving;
        OperationProgress = null;
        try
        {
            Progress<SettingsOperationProgress> progress =
                new(value => OperationProgress = value);
            return await Session.SaveAsync(cancellationToken, progress);
        }
        finally
        {
            if (State == SettingsWindowState.Saving)
            {
                State = SettingsWindowState.Open;
            }
        }
    }

    public bool TryBeginMaintenance()
    {
        if (State != SettingsWindowState.Open)
        {
            return false;
        }

        State = SettingsWindowState.Saving;
        return true;
    }

    public void EndMaintenance()
    {
        if (State == SettingsWindowState.Saving)
        {
            State = SettingsWindowState.Open;
        }
    }

    public async Task<bool> CanCloseAsync(CancellationToken cancellationToken = default)
    {
        if (AllowClose)
        {
            return true;
        }

        if (State is SettingsWindowState.Saving or SettingsWindowState.Closing)
        {
            return false;
        }

        if (!Session.IsDirty)
        {
            AllowClose = true;
            State = SettingsWindowState.Closing;
            return true;
        }

        State = SettingsWindowState.Closing;
        DialogChoice choice = await _dialogs.ShowAsync(
            new DialogRequest(
                _localizer.Get("Settings_Discard_Title"),
                _localizer.Get("Settings_Discard_Message"),
                _localizer.Get("Settings_Discard_Confirm"),
                null,
                _localizer.Get("Settings_Discard_Continue")),
            cancellationToken);
        if (choice == DialogChoice.Primary)
        {
            Session.Cancel();
            AllowClose = true;
            return true;
        }

        State = SettingsWindowState.Open;
        return false;
    }

    public void MarkClosed() => State = SettingsWindowState.Closed;
}

public sealed class GeneralSettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class DictionaryLibrarySettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class AppearanceSettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class ReaderSettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class SecuritySettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class StorageSettingsViewModel(SettingsEditSession session)
{
    public SettingsEditSession Session { get; } = session;
}

public sealed class AboutViewModel
{
    public string ProductName => "VDict";
}
