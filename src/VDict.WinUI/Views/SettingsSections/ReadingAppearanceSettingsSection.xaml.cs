using Microsoft.UI.Xaml.Controls;

namespace VDict.WinUI.Views.SettingsSections;

public sealed partial class ReadingAppearanceSettingsSection : UserControl
{
    public ReadingAppearanceSettingsSection()
    {
        InitializeComponent();
    }

    public ComboBox LanguageSelector => LanguageSelectorControl;
    public ComboBox ThemeSelector => ThemeSelectorControl;
    public NumberBox AppFontSizeBox => AppFontSizeBoxControl;
    public NumberBox AppLineHeightBox => AppLineHeightBoxControl;
    public AutoSuggestBox LatinFontBox => LatinFontBoxControl;
    public AutoSuggestBox CjkFontBox => CjkFontBoxControl;
    public NumberBox ZoomBox => ZoomBoxControl;
    public NumberBox ContentWidthBox => ContentWidthBoxControl;
    public ToggleSwitch BrightnessToggle => BrightnessToggleControl;
    public Button RestoreDefaultsButton => RestoreReaderDefaultsButton;
}
