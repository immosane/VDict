using Microsoft.UI.Xaml.Controls;

namespace VDict.WinUI.Views.SettingsSections;

public sealed partial class DictionarySettingsSection : UserControl
{
    public DictionarySettingsSection()
    {
        InitializeComponent();
    }

    public Button AddButton => AddDictionaryButton;
    public AppBarButton MoveUpButton => MoveDictionaryUpButton;
    public AppBarButton MoveDownButton => MoveDictionaryDownButton;
    public AppBarButton RemoveButton => RemoveDictionaryButton;
    public Grid DictionaryLayout => DictionaryLayoutControl;
    public ColumnDefinition DictionaryListColumn => DictionaryListColumnControl;
    public Border DictionaryEditorPanel => DictionaryEditorPanelControl;
    public ListView DictionaryList => DictionaryListControl;
    public TextBlock DictionaryCountText => DictionaryCountTextControl;
    public TextBlock DictionaryEmptyState => DictionaryEmptyStateControl;
    public TextBlock DictionaryEditorEmptyState => DictionaryEditorEmptyStateControl;
    public StackPanel DictionaryEditor => DictionaryEditorControl;
    public TextBlock SelectedDictionaryNameText => SelectedDictionaryNameTextControl;
    public ToggleSwitch DictionaryEnabledToggle => DictionaryEnabledToggleControl;
    public ToggleSwitch DictionaryBlockNetworkToggle => DictionaryBlockNetworkToggleControl;
    public TextBox DictionaryDisplayNameBox => DictionaryDisplayNameBoxControl;
    public TextBox DictionaryMdxPathBox => DictionaryMdxPathBoxControl;
    public TextBlock DictionaryIndexStatusText => DictionaryIndexStatusTextControl;
    public TextBox DictionaryMddPathsBox => DictionaryMddPathsBoxControl;
    public TextBox DictionaryCssPathsBox => DictionaryCssPathsBoxControl;
    public TextBox DictionaryScriptPathsBox => DictionaryScriptPathsBoxControl;
    public TextBlock DictionaryAutoScriptPathsText => DictionaryAutoScriptPathsTextControl;
    public ToggleSwitch DictionaryOverrideFontToggle => DictionaryOverrideFontToggleControl;
    public ToggleSwitch DictionaryOverrideTypographyToggle =>
        DictionaryOverrideTypographyToggleControl;
    public NumberBox DictionaryFontSizeBox => DictionaryFontSizeBoxControl;
    public NumberBox DictionaryLineHeightBox => DictionaryLineHeightBoxControl;
    public TextBox DictionaryLatinFontBox => DictionaryLatinFontBoxControl;
    public TextBox DictionaryCjkFontBox => DictionaryCjkFontBoxControl;
    public ComboBox DictionaryDarkModeSelector => DictionaryDarkModeSelectorControl;
    public Button RestoreAppearanceButton => RestoreDictionaryAppearanceButton;
}
