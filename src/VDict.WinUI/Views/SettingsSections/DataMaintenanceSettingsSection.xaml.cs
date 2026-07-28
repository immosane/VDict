using Microsoft.UI.Xaml.Controls;

namespace VDict.WinUI.Views.SettingsSections;

public sealed partial class DataMaintenanceSettingsSection : UserControl
{
    public DataMaintenanceSettingsSection()
    {
        InitializeComponent();
    }

    public Button ImportButton => ImportSettingsButton;
    public Button ExportButton => ExportSettingsButton;
    public Button OpenDataButton => OpenDataFolderButton;
    public Button OpenLogsButton => OpenLogsFolderButton;
    public Button InspectButton => InspectCacheButton;
    public TextBlock CacheUsageText => CacheUsageTextControl;
    public Button ClearCacheButton => ClearTemporaryCacheButton;
    public Button ClearBrowserCacheButton => ClearBrowserDataButton;
    public Button RebuildButton => RebuildIndexesButton;
    public Button RestoreDefaultsButton => RestoreAllDefaultsButton;
}
