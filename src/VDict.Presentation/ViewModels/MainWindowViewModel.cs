using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VDict.Presentation.Services;

namespace VDict.Presentation.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(ReaderViewModel reader, IModalWindowService modalWindows)
    {
        Reader = reader;
        OpenSettingsCommand = new AsyncRelayCommand(
            async () =>
            {
                await Reader.FlushZoomAsync().ConfigureAwait(true);
                await modalWindows.ShowSettingsAsync().ConfigureAwait(true);
            },
            () => Reader.CatalogState == CatalogLifecycleState.Ready);
        Reader.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ReaderViewModel.CatalogState))
            {
                OpenSettingsCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public ReaderViewModel Reader { get; }

    public IAsyncRelayCommand OpenSettingsCommand { get; }
}
