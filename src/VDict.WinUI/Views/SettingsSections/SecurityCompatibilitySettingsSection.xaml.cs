using Microsoft.UI.Xaml.Controls;

namespace VDict.WinUI.Views.SettingsSections;

public sealed partial class SecurityCompatibilitySettingsSection : UserControl
{
    public SecurityCompatibilitySettingsSection()
    {
        InitializeComponent();
    }

    public ComboBox ExternalLinkSelector => ExternalLinkSelectorControl;
}
