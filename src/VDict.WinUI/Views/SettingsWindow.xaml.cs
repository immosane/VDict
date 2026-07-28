using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Infrastructure.Catalog;
using VDict.Infrastructure.Indexing;
using VDict.Infrastructure.Maintenance;
using VDict.Infrastructure.Settings;
using VDict.Presentation.Services;
using VDict.Presentation.Settings;
using VDict.Presentation.ViewModels;
using VDict.WinUI.Controls;
using VDict.WinUI.Platform;
using VDict.WinUI.Views.SettingsSections;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;
using CoreApplicationTheme = VDict.Core.Models.ApplicationTheme;

namespace VDict.WinUI.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly WindowDialogService _dialogs;
    private readonly IAppLocalizer _localizer;
    private readonly IAppLogger _logger;
    private readonly IFilePickerService _filePicker;
    private readonly AtomicSettingsStore _settingsStore;
    private readonly IAppDataPaths _paths;
    private readonly CacheMaintenanceService _cacheMaintenance;
    private readonly DictionaryFileInspector _dictionaryFileInspector;
    private readonly DictionaryIndexBuilder _dictionaryIndexBuilder;
    private readonly ReaderViewModel _reader;
    private readonly ReaderWebViewHost _readerHost;
    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private readonly IReadOnlyList<string> _installedFonts;
    private readonly SettingsSectionCache<FrameworkElement> _sectionCache;
    private readonly ObservableCollection<DictionarySettingsListItem> _dictionaryItems = [];
    private readonly PointerEventHandler _comboBoxPointerPressedHandler;
    private CancellationTokenSource? _operationCts;
    private DictionarySettingsSection? _dictionarySection;
    private ReadingAppearanceSettingsSection? _readingSection;
    private SecurityCompatibilitySettingsSection? _securitySection;
    private DataMaintenanceSettingsSection? _dataSection;
    private AboutSettingsSection? _aboutSection;
    private DictionaryId? _selectedDictionaryId;
    private string? _activeSectionKey;
    private bool _enforcingMinimumSize;
    private bool _maintenanceOperation;
    private bool _updatingControls;
    private bool _closeWhenSafe;
    private bool _discardChangesWhenSafe;
    private bool _confirmingClose;

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        WindowDialogService dialogs,
        IAppLocalizer localizer,
        IAppLogger logger,
        IFilePickerService filePicker,
        PickerWindowContext pickerWindow,
        AtomicSettingsStore settingsStore,
        CacheMaintenanceService cacheMaintenance,
        DictionaryFileInspector dictionaryFileInspector,
        DictionaryIndexBuilder dictionaryIndexBuilder,
        ReaderViewModel reader,
        ReaderWebViewHost readerHost,
        IAppDataPaths paths,
        IFontCatalogService fonts,
        WindowsCapabilities capabilities)
    {
        ViewModel = viewModel;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _cacheMaintenance = cacheMaintenance;
        _dictionaryFileInspector = dictionaryFileInspector;
        _dictionaryIndexBuilder = dictionaryIndexBuilder;
        _reader = reader;
        _readerHost = readerHost;
        _paths = paths;
        _comboBoxPointerPressedHandler = ComboBox_PointerPressed;
        InitializeComponent();
        Title = _localizer.Get("SettingsWindow.Title");
        pickerWindow.Attach(this);
        dialogs.Attach(Root);
        if (capabilities.CanUseMica)
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }

        _hwnd = WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowIcon.Apply(_appWindow);
        WindowSizing.ResizeToWorkArea(_appWindow, _hwnd, 960, 720, 760, 520);
        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
        Closed += SettingsWindow_Closed;
        Root.SizeChanged += Root_SizeChanged;
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Session.PropertyChanged += Session_PropertyChanged;
        _installedFonts = fonts.GetInstalledFontFamilies();
        _sectionCache = new SettingsSectionCache<FrameworkElement>(
            CreateSection,
            ReleaseSection);
        ApplyDraftAppearance();
        UpdateResponsiveLayout(Root.ActualWidth);
        UpdateSaveState();
        SettingsNavigation.SelectedItem = SettingsNavigation.MenuItems[0];
    }

    public SettingsWindowViewModel ViewModel { get; }

    private ListView DictionaryList => _dictionarySection!.DictionaryList;
    private Grid DictionaryLayout => _dictionarySection!.DictionaryLayout;
    private ColumnDefinition DictionaryListColumn => _dictionarySection!.DictionaryListColumn;
    private Border DictionaryEditorPanel => _dictionarySection!.DictionaryEditorPanel;
    private TextBlock DictionaryCountText => _dictionarySection!.DictionaryCountText;
    private TextBlock DictionaryEmptyState => _dictionarySection!.DictionaryEmptyState;
    private TextBlock DictionaryEditorEmptyState => _dictionarySection!.DictionaryEditorEmptyState;
    private StackPanel DictionaryEditor => _dictionarySection!.DictionaryEditor;
    private TextBlock SelectedDictionaryNameText => _dictionarySection!.SelectedDictionaryNameText;
    private ToggleSwitch DictionaryEnabledToggle => _dictionarySection!.DictionaryEnabledToggle;
    private ToggleSwitch DictionaryBlockNetworkToggle => _dictionarySection!.DictionaryBlockNetworkToggle;
    private TextBox DictionaryDisplayNameBox => _dictionarySection!.DictionaryDisplayNameBox;
    private TextBox DictionaryMdxPathBox => _dictionarySection!.DictionaryMdxPathBox;
    private TextBlock DictionaryIndexStatusText => _dictionarySection!.DictionaryIndexStatusText;
    private TextBox DictionaryMddPathsBox => _dictionarySection!.DictionaryMddPathsBox;
    private TextBox DictionaryCssPathsBox => _dictionarySection!.DictionaryCssPathsBox;
    private TextBox DictionaryScriptPathsBox => _dictionarySection!.DictionaryScriptPathsBox;
    private TextBlock DictionaryAutoScriptPathsText =>
        _dictionarySection!.DictionaryAutoScriptPathsText;
    private ToggleSwitch DictionaryOverrideFontToggle =>
        _dictionarySection!.DictionaryOverrideFontToggle;
    private ToggleSwitch DictionaryOverrideTypographyToggle =>
        _dictionarySection!.DictionaryOverrideTypographyToggle;
    private NumberBox DictionaryFontSizeBox => _dictionarySection!.DictionaryFontSizeBox;
    private NumberBox DictionaryLineHeightBox => _dictionarySection!.DictionaryLineHeightBox;
    private TextBox DictionaryLatinFontBox => _dictionarySection!.DictionaryLatinFontBox;
    private TextBox DictionaryCjkFontBox => _dictionarySection!.DictionaryCjkFontBox;
    private ComboBox DictionaryDarkModeSelector => _dictionarySection!.DictionaryDarkModeSelector;
    private ComboBox LanguageSelector => _readingSection!.LanguageSelector;
    private ComboBox ThemeSelector => _readingSection!.ThemeSelector;
    private NumberBox AppFontSizeBox => _readingSection!.AppFontSizeBox;
    private NumberBox AppLineHeightBox => _readingSection!.AppLineHeightBox;
    private AutoSuggestBox LatinFontBox => _readingSection!.LatinFontBox;
    private AutoSuggestBox CjkFontBox => _readingSection!.CjkFontBox;
    private NumberBox ZoomBox => _readingSection!.ZoomBox;
    private NumberBox ContentWidthBox => _readingSection!.ContentWidthBox;
    private ToggleSwitch BrightnessToggle => _readingSection!.BrightnessToggle;
    private ComboBox ExternalLinkSelector => _securitySection!.ExternalLinkSelector;
    private TextBlock CacheUsageText => _dataSection!.CacheUsageText;

    public void RequestClose(bool discardChanges = false)
    {
        if (_operationCts is not null)
        {
            _closeWhenSafe = true;
            _discardChangesWhenSafe |= discardChanges;
            _operationCts.Cancel();
            return;
        }

        if (discardChanges)
        {
            ViewModel.Session.Cancel();
            ViewModel.AllowClose = true;
        }

        Close();
    }

    private void SettingsNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        string tag = item.Tag?.ToString() ?? "dictionaries";
        CommitPendingEdits(_activeSectionKey);
        SettingsSectionHost.Content = GetOrCreateSection(tag);
        _activeSectionKey = tag;
        RefreshSection(tag);
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        if (SettingsNavigation.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal)
        {
            SettingsNavigation.IsPaneOpen = false;
        }
    }

    private FrameworkElement GetOrCreateSection(string key)
        => _sectionCache.GetOrCreate(key);

    private FrameworkElement CreateSection(string key)
    {
        FrameworkElement section = key switch
        {
            "dictionaries" => _dictionarySection = new DictionarySettingsSection(),
            "reading" => _readingSection = new ReadingAppearanceSettingsSection(),
            "security" => _securitySection = new SecurityCompatibilitySettingsSection(),
            "data" => _dataSection = new DataMaintenanceSettingsSection(),
            "about" => _aboutSection = new AboutSettingsSection(),
            _ => throw new InvalidOperationException($"未知的设置 Section：{key}")
        };
        section.DataContext = ViewModel;
        WireSection(section);
        return section;
    }

    private void ReleaseSection(FrameworkElement section)
    {
        UnwireSection(section);
        section.DataContext = null;
    }

    private void WireSection(FrameworkElement section)
    {
        switch (section)
        {
            case DictionarySettingsSection dictionary:
                dictionary.DictionaryList.ItemsSource = _dictionaryItems;
                dictionary.AddButton.Click += AddDictionary_Click;
                dictionary.MoveUpButton.Click += MoveDictionaryUp_Click;
                dictionary.MoveDownButton.Click += MoveDictionaryDown_Click;
                dictionary.RemoveButton.Click += RemoveDictionary_Click;
                dictionary.DictionaryList.SelectionChanged += DictionaryList_SelectionChanged;
                dictionary.DictionaryList.DragOver += DictionaryList_DragOver;
                dictionary.DictionaryList.Drop += DictionaryList_Drop;
                dictionary.DictionaryEnabledToggle.Toggled += DictionaryEnabledToggle_Toggled;
                dictionary.DictionaryBlockNetworkToggle.Toggled += DictionaryBlockNetworkToggle_Toggled;
                dictionary.DictionaryDisplayNameBox.LostFocus +=
                    DictionaryDisplayNameBox_LostFocus;
                dictionary.DictionaryOverrideFontToggle.Toggled +=
                    DictionaryOverrideFontToggle_Toggled;
                dictionary.DictionaryOverrideTypographyToggle.Toggled +=
                    DictionaryOverrideTypographyToggle_Toggled;
                dictionary.DictionaryFontSizeBox.ValueChanged +=
                    DictionaryFontSizeBox_ValueChanged;
                dictionary.DictionaryLineHeightBox.ValueChanged +=
                    DictionaryLineHeightBox_ValueChanged;
                dictionary.DictionaryLatinFontBox.LostFocus +=
                    DictionaryLatinFontBox_LostFocus;
                dictionary.DictionaryCjkFontBox.LostFocus += DictionaryCjkFontBox_LostFocus;
                dictionary.DictionaryDarkModeSelector.SelectionChanged +=
                    DictionaryDarkModeSelector_SelectionChanged;
                WireComboBox(dictionary.DictionaryDarkModeSelector);
                dictionary.RestoreAppearanceButton.Click += RestoreDictionaryAppearance_Click;
                break;
            case ReadingAppearanceSettingsSection reading:
                reading.LanguageSelector.SelectionChanged += LanguageSelector_SelectionChanged;
                reading.ThemeSelector.SelectionChanged += ThemeSelector_SelectionChanged;
                WireComboBox(reading.LanguageSelector);
                WireComboBox(reading.ThemeSelector);
                reading.AppFontSizeBox.ValueChanged += AppFontSizeBox_ValueChanged;
                reading.AppLineHeightBox.ValueChanged += AppLineHeightBox_ValueChanged;
                reading.LatinFontBox.LostFocus += LatinFontBox_LostFocus;
                reading.CjkFontBox.LostFocus += CjkFontBox_LostFocus;
                reading.ZoomBox.ValueChanged += ZoomBox_ValueChanged;
                reading.ContentWidthBox.ValueChanged += ContentWidthBox_ValueChanged;
                reading.BrightnessToggle.Toggled += BrightnessToggle_Toggled;
                reading.RestoreDefaultsButton.Click += RestoreReaderDefaults_Click;
                reading.LatinFontBox.ItemsSource = _installedFonts;
                reading.CjkFontBox.ItemsSource = _installedFonts;
                break;
            case SecurityCompatibilitySettingsSection security:
                security.ExternalLinkSelector.SelectionChanged +=
                    ExternalLinkSelector_SelectionChanged;
                WireComboBox(security.ExternalLinkSelector);
                break;
            case DataMaintenanceSettingsSection data:
                data.ImportButton.Click += ImportSettings_Click;
                data.ExportButton.Click += ExportSettings_Click;
                data.OpenDataButton.Click += OpenDataFolder_Click;
                data.OpenLogsButton.Click += OpenLogsFolder_Click;
                data.InspectButton.Click += InspectCache_Click;
                data.ClearCacheButton.Click += ClearTemporaryCache_Click;
                data.ClearBrowserCacheButton.Click += ClearBrowserCache_Click;
                data.RebuildButton.Click += RebuildIndexes_Click;
                data.RestoreDefaultsButton.Click += RestoreAllDefaults_Click;
                break;
        }
    }

    private void UnwireSection(FrameworkElement section)
    {
        switch (section)
        {
            case DictionarySettingsSection dictionary:
                dictionary.AddButton.Click -= AddDictionary_Click;
                dictionary.MoveUpButton.Click -= MoveDictionaryUp_Click;
                dictionary.MoveDownButton.Click -= MoveDictionaryDown_Click;
                dictionary.RemoveButton.Click -= RemoveDictionary_Click;
                dictionary.DictionaryList.SelectionChanged -= DictionaryList_SelectionChanged;
                dictionary.DictionaryList.DragOver -= DictionaryList_DragOver;
                dictionary.DictionaryList.Drop -= DictionaryList_Drop;
                dictionary.DictionaryEnabledToggle.Toggled -= DictionaryEnabledToggle_Toggled;
                dictionary.DictionaryBlockNetworkToggle.Toggled -= DictionaryBlockNetworkToggle_Toggled;
                dictionary.DictionaryDisplayNameBox.LostFocus -=
                    DictionaryDisplayNameBox_LostFocus;
                dictionary.DictionaryOverrideFontToggle.Toggled -=
                    DictionaryOverrideFontToggle_Toggled;
                dictionary.DictionaryOverrideTypographyToggle.Toggled -=
                    DictionaryOverrideTypographyToggle_Toggled;
                dictionary.DictionaryFontSizeBox.ValueChanged -=
                    DictionaryFontSizeBox_ValueChanged;
                dictionary.DictionaryLineHeightBox.ValueChanged -=
                    DictionaryLineHeightBox_ValueChanged;
                dictionary.DictionaryLatinFontBox.LostFocus -=
                    DictionaryLatinFontBox_LostFocus;
                dictionary.DictionaryCjkFontBox.LostFocus -= DictionaryCjkFontBox_LostFocus;
                dictionary.DictionaryDarkModeSelector.SelectionChanged -=
                    DictionaryDarkModeSelector_SelectionChanged;
                UnwireComboBox(dictionary.DictionaryDarkModeSelector);
                dictionary.RestoreAppearanceButton.Click -= RestoreDictionaryAppearance_Click;
                dictionary.DictionaryList.ItemsSource = null;
                break;
            case ReadingAppearanceSettingsSection reading:
                reading.LanguageSelector.SelectionChanged -= LanguageSelector_SelectionChanged;
                reading.ThemeSelector.SelectionChanged -= ThemeSelector_SelectionChanged;
                UnwireComboBox(reading.LanguageSelector);
                UnwireComboBox(reading.ThemeSelector);
                reading.AppFontSizeBox.ValueChanged -= AppFontSizeBox_ValueChanged;
                reading.AppLineHeightBox.ValueChanged -= AppLineHeightBox_ValueChanged;
                reading.LatinFontBox.LostFocus -= LatinFontBox_LostFocus;
                reading.CjkFontBox.LostFocus -= CjkFontBox_LostFocus;
                reading.ZoomBox.ValueChanged -= ZoomBox_ValueChanged;
                reading.ContentWidthBox.ValueChanged -= ContentWidthBox_ValueChanged;
                reading.BrightnessToggle.Toggled -= BrightnessToggle_Toggled;
                reading.RestoreDefaultsButton.Click -= RestoreReaderDefaults_Click;
                break;
            case SecurityCompatibilitySettingsSection security:
                security.ExternalLinkSelector.SelectionChanged -=
                    ExternalLinkSelector_SelectionChanged;
                UnwireComboBox(security.ExternalLinkSelector);
                break;
            case DataMaintenanceSettingsSection data:
                data.ImportButton.Click -= ImportSettings_Click;
                data.ExportButton.Click -= ExportSettings_Click;
                data.OpenDataButton.Click -= OpenDataFolder_Click;
                data.OpenLogsButton.Click -= OpenLogsFolder_Click;
                data.InspectButton.Click -= InspectCache_Click;
                data.ClearCacheButton.Click -= ClearTemporaryCache_Click;
                data.ClearBrowserCacheButton.Click -= ClearBrowserCache_Click;
                data.RebuildButton.Click -= RebuildIndexes_Click;
                data.RestoreDefaultsButton.Click -= RestoreAllDefaults_Click;
                break;
        }
    }

    private void WireComboBox(ComboBox comboBox) =>
        comboBox.AddHandler(
            UIElement.PointerPressedEvent,
            _comboBoxPointerPressedHandler,
            handledEventsToo: true);

    private void UnwireComboBox(ComboBox comboBox) =>
        comboBox.RemoveHandler(
            UIElement.PointerPressedEvent,
            _comboBoxPointerPressedHandler);

    private static void ComboBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            _ = comboBox.CancelDirectManipulations();
        }
    }

    private void RefreshSection(string key)
    {
        RefreshControls();
        if (key == "dictionaries")
        {
            UpdateResponsiveLayout(
                WindowSizing.PixelsToDip(_hwnd, _appWindow.Size.Width));
        }
    }

    private void CommitPendingEdits(string? sectionKey = null)
    {
        if (_updatingControls)
        {
            return;
        }

        if ((sectionKey is null or "dictionaries") &&
            _dictionarySection is not null &&
            _selectedDictionaryId is { } dictionaryId)
        {
            CommitDictionaryPendingEdits(dictionaryId);
        }

        if ((sectionKey is null or "reading") && _readingSection is not null)
        {
            UpdateApplicationAppearance(appearance => appearance with
            {
                LatinFontFamily = LatinFontBox.Text.Trim(),
                CjkFontFamily = CjkFontBox.Text.Trim()
            });
        }
    }

    private void CommitDictionaryPendingEdits(DictionaryId dictionaryId)
    {
        if (_updatingControls)
        {
            return;
        }

        DictionaryDefinition? current = ViewModel.Session.Draft.Dictionaries
            .FirstOrDefault(item => item.Id == dictionaryId);
        if (current is null)
        {
            return;
        }

        DictionaryDefinition updated = current with
        {
            DisplayName = string.IsNullOrWhiteSpace(DictionaryDisplayNameBox.Text)
                ? null
                : DictionaryDisplayNameBox.Text.Trim(),
            Appearance = current.Appearance with
            {
                LatinFontFamily = DictionaryLatinFontBox.Text.Trim(),
                CjkFontFamily = DictionaryCjkFontBox.Text.Trim()
            }
        };
        if (updated != current)
        {
            ViewModel.Session.ReplaceDictionary(updated);
        }
    }

    private void RefreshControls()
    {
        _updatingControls = true;
        try
        {
            ApplicationSettings draft = ViewModel.Session.Draft;
            if (_securitySection is not null)
            {
                SelectByTag(
                    ExternalLinkSelector,
                    draft.ExternalLinkPolicy == ExternalLinkPolicy.SystemBrowser
                        ? "system-browser"
                        : "block");
            }

            if (_readingSection is not null)
            {
                SelectByTag(LanguageSelector, SettingsSchema.Format(draft.UiLanguage));
                BrightnessToggle.IsOn = draft.ReduceDictionaryBrightnessInDarkMode;
                AppFontSizeBox.Value = draft.Appearance.FontSize;
                AppLineHeightBox.Value = draft.Appearance.LineHeight;
                LatinFontBox.Text = draft.Appearance.LatinFontFamily;
                CjkFontBox.Text = draft.Appearance.CjkFontFamily;
                ZoomBox.Value = draft.Appearance.ContentZoom;
                ContentWidthBox.Value = draft.Appearance.ContentWidthPercent;
                SelectByTag(ThemeSelector, draft.Appearance.Theme switch
                {
                    CoreApplicationTheme.Light => "light",
                    CoreApplicationTheme.Dark => "dark",
                    _ => "system"
                });
            }

            if (_dictionarySection is not null)
            {
                SynchronizeDictionaryItems(draft.Dictionaries);
                DictionarySettingsListItem? selected =
                    FindDictionaryItem(_selectedDictionaryId) ??
                    _dictionaryItems.FirstOrDefault();
                _selectedDictionaryId = selected?.Id;
                if (!ReferenceEquals(DictionaryList.SelectedItem, selected))
                {
                    DictionaryList.SelectedItem = selected;
                }

                PopulateDictionaryEditor(selected?.Definition);
                UpdateDictionaryCommandState();
            }
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void SynchronizeDictionaryItems(
        ImmutableArray<DictionaryDefinition> dictionaries)
    {
        for (int targetIndex = 0; targetIndex < dictionaries.Length; targetIndex++)
        {
            DictionaryDefinition definition = dictionaries[targetIndex];
            int currentIndex = FindDictionaryItemIndex(definition.Id);
            if (currentIndex < 0)
            {
                _dictionaryItems.Insert(
                    targetIndex,
                    new DictionarySettingsListItem(definition));
            }
            else
            {
                if (currentIndex != targetIndex)
                {
                    _dictionaryItems.Move(currentIndex, targetIndex);
                }

                _dictionaryItems[targetIndex].Update(definition);
            }
        }

        while (_dictionaryItems.Count > dictionaries.Length)
        {
            _dictionaryItems.RemoveAt(_dictionaryItems.Count - 1);
        }
    }

    private DictionarySettingsListItem? FindDictionaryItem(DictionaryId? id) =>
        id is { } dictionaryId
            ? _dictionaryItems.FirstOrDefault(item => item.Id == dictionaryId)
            : null;

    private int FindDictionaryItemIndex(DictionaryId id)
    {
        for (int index = 0; index < _dictionaryItems.Count; index++)
        {
            if (_dictionaryItems[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindDictionaryDefinitionIndex(
        ImmutableArray<DictionaryDefinition> dictionaries,
        DictionaryId id)
    {
        for (int index = 0; index < dictionaries.Length; index++)
        {
            if (dictionaries[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateDictionaryCommandState()
    {
        if (_dictionarySection is null)
        {
            return;
        }

        int selectedIndex = DictionaryList.SelectedItem is DictionarySettingsListItem selected
            ? _dictionaryItems.IndexOf(selected)
            : -1;
        _dictionarySection.MoveUpButton.IsEnabled = selectedIndex > 0;
        _dictionarySection.MoveDownButton.IsEnabled =
            selectedIndex >= 0 && selectedIndex < _dictionaryItems.Count - 1;
        _dictionarySection.RemoveButton.IsEnabled = selectedIndex >= 0;
    }

    private void BrightnessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls)
        {
            ViewModel.Session.Update(
                settings => settings with
                {
                    ReduceDictionaryBrightnessInDarkMode = BrightnessToggle.IsOn
                },
                SettingsDirtySection.Reader);
        }
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || LanguageSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ApplicationLanguage language = SettingsSchema.ParseLanguage(item.Tag?.ToString());
        ViewModel.Session.Update(
            settings => settings with { UiLanguage = language },
            SettingsDirtySection.General);
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || ThemeSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        CoreApplicationTheme theme = item.Tag?.ToString() switch
        {
            "light" => CoreApplicationTheme.Light,
            "dark" => CoreApplicationTheme.Dark,
            _ => CoreApplicationTheme.System
        };
        ViewModel.Session.Update(
            settings => settings with
            {
                Appearance = settings.Appearance with { Theme = theme }
            },
            SettingsDirtySection.Appearance);
        ApplyDraftAppearance();
    }

    private void AppFontSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingControls && !double.IsNaN(args.NewValue))
        {
            UpdateApplicationAppearance(
                appearance => appearance with
                {
                    FontSize = Math.Clamp((int)Math.Round(args.NewValue), 10, 48)
                });
        }
    }

    private void AppLineHeightBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingControls && !double.IsNaN(args.NewValue))
        {
            UpdateApplicationAppearance(
                appearance => appearance with
                {
                    LineHeight = Math.Clamp(args.NewValue, 1.0, 3.0)
                });
        }
    }

    private void LatinFontBox_LostFocus(object sender, RoutedEventArgs e) =>
        UpdateApplicationAppearance(
            appearance => appearance with { LatinFontFamily = LatinFontBox.Text.Trim() });

    private void CjkFontBox_LostFocus(object sender, RoutedEventArgs e) =>
        UpdateApplicationAppearance(
            appearance => appearance with { CjkFontFamily = CjkFontBox.Text.Trim() });

    private void ZoomBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingControls && !double.IsNaN(args.NewValue))
        {
            ViewModel.Session.Update(
                settings => settings with
                {
                    Appearance = settings.Appearance with
                    {
                        ContentZoom = Math.Clamp(args.NewValue, 0.5, 2.5)
                    }
                },
                SettingsDirtySection.Reader);
        }
    }

    private void ExternalLinkSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || ExternalLinkSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ExternalLinkPolicy policy = item.Tag?.ToString() == "system-browser"
            ? ExternalLinkPolicy.SystemBrowser
            : ExternalLinkPolicy.Block;
        ViewModel.Session.Update(
            settings => settings with { ExternalLinkPolicy = policy },
            SettingsDirtySection.SecurityAndNetwork);
    }

    private async void AddDictionary_Click(object sender, RoutedEventArgs e)
    {
        string? path = await _filePicker.PickDictionaryAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        AddDictionaryFromPath(path);
    }

    private void RemoveDictionary_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits("dictionaries");
        if (DictionaryList.SelectedItem is DictionarySettingsListItem item)
        {
            int currentIndex = _dictionaryItems.IndexOf(item);
            if (ViewModel.Session.RemoveDictionary(item.Id))
            {
                ImmutableArray<DictionaryDefinition> dictionaries =
                    ViewModel.Session.Draft.Dictionaries;
                _selectedDictionaryId = dictionaries.IsDefaultOrEmpty
                    ? null
                    : dictionaries[Math.Min(currentIndex, dictionaries.Length - 1)].Id;
                RefreshControls();
            }
        }
    }

    private void MoveDictionaryUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedDictionary(-1);

    private void MoveDictionaryDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedDictionary(1);

    private void MoveSelectedDictionary(int offset)
    {
        CommitPendingEdits("dictionaries");
        if (DictionaryList.SelectedItem is not DictionarySettingsListItem item)
        {
            return;
        }

        int current = FindDictionaryDefinitionIndex(
            ViewModel.Session.Draft.Dictionaries,
            item.Id);
        if (current < 0)
        {
            return;
        }

        int destination = current + offset;
        if (ViewModel.Session.MoveDictionary(item.Id, destination))
        {
            _selectedDictionaryId = item.Id;
            RefreshControls();
        }
    }

    private void AddDictionaryFromPath(string path)
    {
        try
        {
            if (!Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                ShowError(_localizer.Get("Settings_AddDictionary_InvalidMdx"));
                return;
            }

            string fullPath = Path.GetFullPath(path);
            if (ViewModel.Session.Draft.Dictionaries.Any(dictionary =>
                    PathsReferToSameFile(dictionary.MdxPath, fullPath)))
            {
                ShowError(_localizer.Get("Settings_AddDictionary_Duplicate"));
                return;
            }

            DictionaryDefinition dictionary =
                _dictionaryFileInspector.CreateDefault(fullPath);
            ViewModel.Session.AddDictionary(dictionary);
            _selectedDictionaryId = dictionary.Id;
            RefreshControls();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                  ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Add dictionary failed.", exception);
            ShowError(_localizer.Get("Settings_AddDictionary_Failed"));
        }
    }

    private static bool PathsReferToSameFile(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void PopulateDictionaryEditor(DictionaryDefinition? dictionary)
    {
        DictionaryCountText.Text = _localizer.Format(
            "Settings_DictionaryCount",
            ViewModel.Session.Draft.Dictionaries.Length);
        DictionaryEmptyState.Visibility =
            ViewModel.Session.Draft.Dictionaries.IsDefaultOrEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
        DictionaryEditorEmptyState.Visibility = dictionary is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        DictionaryEditor.Visibility = dictionary is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (dictionary is null)
        {
            return;
        }

        SelectedDictionaryNameText.Text = dictionary.EffectiveDisplayName;
        ToolTipService.SetToolTip(
            SelectedDictionaryNameText,
            dictionary.EffectiveDisplayName);
        DictionaryEnabledToggle.IsOn = dictionary.Enabled;
        DictionaryBlockNetworkToggle.IsOn = dictionary.BlockNetwork;
        DictionaryDisplayNameBox.Text = dictionary.DisplayName ?? string.Empty;
        DictionaryMdxPathBox.Text = dictionary.MdxPath;
        DictionaryIndexState state = _dictionaryIndexBuilder.GetState(
            dictionary,
            _paths.IndexesDirectory);
        DictionaryIndexStatusText.Text = state switch
        {
            DictionaryIndexState.Available => _localizer.Get("Settings_Index_Available"),
            DictionaryIndexState.Missing => _localizer.Get("Settings_Index_Missing"),
            DictionaryIndexState.Stale => _localizer.Get("Settings_Index_Stale"),
            _ => _localizer.Get("Settings_Index_Invalid")
        };
        DictionaryMddPathsBox.Text = string.Join(System.Environment.NewLine, dictionary.MddPaths);
        DictionaryCssPathsBox.Text = string.Join(System.Environment.NewLine, dictionary.CssPaths);
        DictionaryScriptPathsBox.Text = string.Join(
            System.Environment.NewLine,
            dictionary.ScriptPaths);
        string? automaticScript = DictionaryScriptDiscovery.FindSameStemScript(dictionary.MdxPath);
        DictionaryAutoScriptPathsText.Text = automaticScript is null
            ? _localizer.Get("Settings_AutoScript_NotFound")
            : _localizer.Format("Settings_AutoScript_Found", automaticScript);
        DictionaryOverrideFontToggle.IsOn = dictionary.Appearance.OverrideFont;
        DictionaryOverrideTypographyToggle.IsOn = dictionary.Appearance.OverrideTypography;
        DictionaryFontSizeBox.Value = dictionary.Appearance.FontSize;
        DictionaryLineHeightBox.Value = dictionary.Appearance.LineHeight;
        DictionaryLatinFontBox.Text = dictionary.Appearance.LatinFontFamily;
        DictionaryCjkFontBox.Text = dictionary.Appearance.CjkFontFamily;
        SelectByTag(
            DictionaryDarkModeSelector,
            dictionary.Appearance.DarkModeStrategy switch
            {
                DictionaryDarkModeStrategy.Global => "global",
                DictionaryDarkModeStrategy.Light => "original",
                DictionaryDarkModeStrategy.Dark => "dark",
                _ => "original"
            });
    }

    private void UpdateSelectedDictionary(
        Func<DictionaryDefinition, DictionaryDefinition> update)
    {
        if (_updatingControls ||
            _selectedDictionaryId is not { } selectedId)
        {
            return;
        }

        DictionaryDefinition? selected = ViewModel.Session.Draft.Dictionaries
            .FirstOrDefault(item => item.Id == selectedId);
        if (selected is null)
        {
            return;
        }

        DictionaryDefinition updated = update(selected);
        if (updated == selected || !ViewModel.Session.ReplaceDictionary(updated))
        {
            return;
        }

        _updatingControls = true;
        try
        {
            SynchronizeDictionaryItems(ViewModel.Session.Draft.Dictionaries);
            DictionarySettingsListItem? selectedItem = FindDictionaryItem(updated.Id);
            if (!ReferenceEquals(DictionaryList.SelectedItem, selectedItem))
            {
                DictionaryList.SelectedItem = selectedItem;
            }

            _selectedDictionaryId = updated.Id;
            SelectedDictionaryNameText.Text = updated.EffectiveDisplayName;
            ToolTipService.SetToolTip(
                SelectedDictionaryNameText,
                updated.EffectiveDisplayName);
            UpdateDictionaryCommandState();
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void UpdateApplicationAppearance(
        Func<ApplicationAppearance, ApplicationAppearance> update)
    {
        if (_updatingControls)
        {
            return;
        }

        ApplicationAppearance current = ViewModel.Session.Draft.Appearance;
        ApplicationAppearance updated = update(current);
        if (updated == current)
        {
            return;
        }

        ViewModel.Session.Update(
            settings => settings with { Appearance = updated },
            SettingsDirtySection.Appearance);
        ApplyDraftAppearance();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private void DictionaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls)
        {
            DictionaryId? nextId =
                (DictionaryList.SelectedItem as DictionarySettingsListItem)?.Id;
            if (_selectedDictionaryId is { } previousId)
            {
                CommitDictionaryPendingEdits(previousId);
            }

            _updatingControls = true;
            try
            {
                SynchronizeDictionaryItems(ViewModel.Session.Draft.Dictionaries);
                DictionarySettingsListItem? selected = FindDictionaryItem(nextId);
                if (!ReferenceEquals(DictionaryList.SelectedItem, selected))
                {
                    DictionaryList.SelectedItem = selected;
                }

                _selectedDictionaryId = selected?.Id;
                PopulateDictionaryEditor(selected?.Definition);
                UpdateDictionaryCommandState();
            }
            finally
            {
                _updatingControls = false;
            }
        }
    }

    private void DictionaryList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _localizer.Get("Settings_Drag_AddMdx");
        }
    }

    private async void DictionaryList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            foreach (StorageFile file in items.OfType<StorageFile>()
                         .Where(file => file.FileType.Equals(".mdx", StringComparison.OrdinalIgnoreCase)))
            {
                AddDictionaryFromPath(file.Path);
            }
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Drop dictionary failed.", exception);
            ShowError(_localizer.Get("Settings_AddDictionary_Failed"));
        }
    }

    private void DictionaryEnabledToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateSelectedDictionary(
            dictionary => dictionary with { Enabled = DictionaryEnabledToggle.IsOn });

    private async void DictionaryBlockNetworkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!DictionaryBlockNetworkToggle.IsOn)
        {
            ContentDialog dialog = new()
            {
                Title = _localizer.Get("Settings_NetworkIsolation_Title"),
                Content = _localizer.Get("Settings_NetworkIsolation_Message"),
                PrimaryButtonText = _localizer.Get("Settings_NetworkIsolation_KeepBlocking"),
                SecondaryButtonText = _localizer.Get("Settings_NetworkIsolation_Disable"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Secondary)
            {
                DictionaryBlockNetworkToggle.IsOn = true;
                return;
            }
        }

        UpdateSelectedDictionary(
            dictionary => dictionary with { BlockNetwork = DictionaryBlockNetworkToggle.IsOn });
    }

    private void DictionaryDisplayNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedDictionaryId is { } dictionaryId)
        {
            CommitDictionaryPendingEdits(dictionaryId);
            _updatingControls = true;
            try
            {
                SynchronizeDictionaryItems(ViewModel.Session.Draft.Dictionaries);
            }
            finally
            {
                _updatingControls = false;
            }
        }
    }

    private void ContentWidthBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingControls && !double.IsNaN(args.NewValue))
        {
            ViewModel.Session.Update(
                settings => settings with
                {
                    Appearance = settings.Appearance with
                    {
                        ContentWidthPercent = Math.Clamp(
                            (int)Math.Round(args.NewValue),
                            30,
                            100)
                    }
                },
                SettingsDirtySection.Reader);
        }
    }

    private void DictionaryOverrideFontToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateSelectedDictionary(
            dictionary => dictionary with
            {
                Appearance = dictionary.Appearance with
                {
                    OverrideFont = DictionaryOverrideFontToggle.IsOn
                }
            });

    private void DictionaryOverrideTypographyToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateSelectedDictionary(
            dictionary => dictionary with
            {
                Appearance = dictionary.Appearance with
                {
                    OverrideTypography = DictionaryOverrideTypographyToggle.IsOn
                }
            });

    private void DictionaryFontSizeBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
        {
            UpdateSelectedDictionary(
                dictionary => dictionary with
                {
                    Appearance = dictionary.Appearance with
                    {
                        FontSize = Math.Clamp((int)Math.Round(args.NewValue), 10, 48)
                    }
                });
        }
    }

    private void DictionaryLineHeightBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
        {
            UpdateSelectedDictionary(
                dictionary => dictionary with
                {
                    Appearance = dictionary.Appearance with
                    {
                        LineHeight = Math.Clamp(args.NewValue, 1.0, 3.0)
                    }
                });
        }
    }

    private void DictionaryLatinFontBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedDictionaryId is { } dictionaryId)
        {
            CommitDictionaryPendingEdits(dictionaryId);
        }
    }

    private void DictionaryCjkFontBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedDictionaryId is { } dictionaryId)
        {
            CommitDictionaryPendingEdits(dictionaryId);
        }
    }

    private void DictionaryDarkModeSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DictionaryDarkModeSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        DictionaryDarkModeStrategy strategy = item.Tag?.ToString() switch
        {
            "global" => DictionaryDarkModeStrategy.Global,
            "original" or "light" => DictionaryDarkModeStrategy.Light,
            "dark" => DictionaryDarkModeStrategy.Dark,
            _ => DictionaryDarkModeStrategy.Light
        };
        UpdateSelectedDictionary(
            dictionary => dictionary with
            {
                Appearance = dictionary.Appearance with { DarkModeStrategy = strategy }
            });
    }

    private void RestoreReaderDefaults_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Session.RestoreReaderDefaults();
        RefreshControls();
        ApplyDraftAppearance();
    }

    private void RestoreDictionaryAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DictionaryList.SelectedItem is DictionarySettingsListItem item)
        {
            ViewModel.Session.RestoreDictionaryAppearance(item.Id);
        }

        RefreshControls();
        ApplyDraftAppearance();
    }

    private void RestoreAllDefaults_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Session.RestoreAllDefaults();
        RefreshControls();
        ApplyDraftAppearance();
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        string? source = await _filePicker.PickSettingsImportAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        try
        {
            ApplicationSettings imported = await _settingsStore.ImportAsync(source).ConfigureAwait(true);
            ViewModel.Session.Update(
                _ => imported with { Revision = ViewModel.Session.Original.Revision },
                SettingsDirtySection.General |
                SettingsDirtySection.Dictionaries |
                SettingsDirtySection.Appearance |
                SettingsDirtySection.Reader |
                SettingsDirtySection.SecurityAndNetwork |
                SettingsDirtySection.StorageAndCache);
            RefreshControls();
            ApplyDraftAppearance();
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Import settings failed.", exception);
            ShowError(_localizer.Get("Settings_Import_Failed"));
        }
    }

    private async void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        string? destination = await _filePicker.PickSettingsExportAsync("vdict-settings.json")
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        try
        {
            await _settingsStore.ExportAsync(destination, ViewModel.Session.Draft).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Export settings failed.", exception);
            ShowError(_localizer.Get("Settings_Export_Failed"));
        }
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        await OpenFolderAsync(_paths.DataRoot).ConfigureAwait(true);

    private async void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = _localizer.Get("Settings_OpenLogs_Title"),
            Content = _localizer.Get("Settings_OpenLogs_Message"),
            PrimaryButtonText = _localizer.Get("Settings_OpenLogs_Open"),
            CloseButtonText = _localizer.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await OpenFolderAsync(_paths.LogsDirectory).ConfigureAwait(true);
        }
    }

    private async void InspectCache_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation(isMaintenance: true))
        {
            return;
        }

        try
        {
            ImmutableArray<CacheCategoryUsage> usage =
                await _cacheMaintenance.InspectAsync(_operationCts!.Token).ConfigureAwait(true);
            CacheUsageText.Text = string.Join(
                System.Environment.NewLine,
                usage.Select(item =>
                    _localizer.Format(
                        "Settings_CacheUsage_Base",
                        item.Category,
                        FormatBytes(item.Bytes),
                        item.FileCount) +
                    (item.Runtime is null
                        ? string.Empty
                        : _localizer.Format(
                            "Settings_CacheUsage_Runtime",
                            FormatBytes(item.Runtime.CapacityBytes),
                            item.Runtime.Hits,
                            item.Runtime.Misses,
                            item.Runtime.Evictions,
                            item.Runtime.OversizedRejections)) +
                    (string.IsNullOrWhiteSpace(item.Error)
                        ? string.Empty
                        : _localizer.Get("Settings_CacheUsage_Error"))));
        }
        catch (OperationCanceledException)
        {
            CacheUsageText.Text = _localizer.Get("Settings_CacheInspect_Canceled");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ClearTemporaryCache_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation(isMaintenance: true))
        {
            return;
        }

        try
        {
            _reader.CancelActiveOperations();
            await _readerHost.PrepareForCacheMaintenanceAsync().ConfigureAwait(true);
            CacheCleanupResult result = await _cacheMaintenance.CleanAsync(
                CacheCategory.Resources | CacheCategory.Temp,
                _operationCts!.Token,
                new Progress<SettingsOperationProgress>(UpdateOperationProgress)).ConfigureAwait(true);
            if (!result.Errors.IsDefaultOrEmpty)
            {
                ShowError(_localizer.Get("Settings_CacheClear_Failed"));
            }
        }
        catch (OperationCanceledException)
        {
            ShowError(_localizer.Get("Settings_CacheClear_Canceled"));
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ClearBrowserCache_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation(isMaintenance: true))
        {
            return;
        }

        try
        {
            _reader.CancelActiveOperations();
            await _readerHost.ClearBrowsingDataAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Browser cache clear failed.", exception);
            ShowError(_localizer.Get("Settings_BrowserCacheClear_Failed"));
        }
        finally
        {
            await RefreshReaderAfterMaintenanceAsync().ConfigureAwait(true);
            EndOperation();
        }
    }

    private async void RebuildIndexes_Click(object sender, RoutedEventArgs e)
    {
        DialogChoice choice = await _dialogs.ShowAsync(new DialogRequest(
            _localizer.Get("Settings_RebuildIndexes_Title"),
            _localizer.Get("Settings_RebuildIndexes_Message"),
            _localizer.Get("Settings_RebuildIndexes_Confirm"),
            null,
            _localizer.Get("Common_Cancel"))).ConfigureAwait(true);
        if (choice != DialogChoice.Primary)
        {
            return;
        }

        if (!BeginOperation(isMaintenance: true))
        {
            return;
        }

        try
        {
            _reader.CancelActiveOperations();
            await _readerHost.PrepareForCacheMaintenanceAsync().ConfigureAwait(true);
            CacheCleanupResult result = await _cacheMaintenance.CleanAsync(
                    CacheCategory.Indexes,
                    _operationCts!.Token,
                    new Progress<SettingsOperationProgress>(UpdateOperationProgress))
                .ConfigureAwait(true);
            if (!result.Errors.IsDefaultOrEmpty)
            {
                ShowError(_localizer.Get("Settings_RebuildIndexes_Failed"));
            }
        }
        catch (OperationCanceledException)
        {
            ShowError(_localizer.Get("Settings_RebuildIndexes_Canceled"));
        }
        finally
        {
            await RefreshReaderAfterMaintenanceAsync().ConfigureAwait(true);
            EndOperation();
        }
    }

    private async Task RefreshReaderAfterMaintenanceAsync()
    {
        _readerHost.ResumeAfterCacheMaintenance();
        string currentWord = _reader.CurrentWord;
        if (string.IsNullOrWhiteSpace(currentWord))
        {
            _reader.RefreshWelcome();
            return;
        }

        await _reader.LookupAsync(
            currentWord,
            recordHistory: false,
            origin: LookupOrigin.ScopeRefresh).ConfigureAwait(true);
    }

    private async Task OpenFolderAsync(string path)
    {
        try
        {
            _paths.EnsureCreated();
            Windows.Storage.StorageFolder folder =
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path);
            _ = await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "settings", "Open folder failed.", exception);
            ShowError(_localizer.Get("Settings_OpenFolder_Failed"));
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
        if (!BeginOperation(isMaintenance: false))
        {
            return;
        }

        ApplicationLanguage languageBeforeSave = ViewModel.Session.Original.UiLanguage;
        try
        {
            SettingsCommitResult result = await ViewModel.SaveAsync(_operationCts!.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ShowError(result.Error ?? _localizer.Get("Settings_Save_Failed"));
            }
            else
            {
                SaveError.IsOpen = false;
                RefreshControls();
                if (languageBeforeSave != ViewModel.Session.Original.UiLanguage)
                {
                    await PromptForLanguageRestartAsync().ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            ShowError(_localizer.Get("Settings_Save_Canceled"));
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PromptForLanguageRestartAsync()
    {
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = _localizer.Get("LanguageRestart_Title"),
            Content = _localizer.Get("LanguageRestart_Message"),
            PrimaryButtonText = _localizer.Get("LanguageRestart_Now"),
            CloseButtonText = _localizer.Get("LanguageRestart_Later"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Windows.ApplicationModel.Core.AppRestartFailureReason failure =
            AppInstance.Restart(string.Empty);
        ShowError(_localizer.Format("LanguageRestart_Failed", failure));
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) =>
        _operationCts?.Cancel();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.State == SettingsWindowState.Saving)
        {
            return;
        }

        ViewModel.Session.Cancel();
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (ViewModel.AllowClose)
        {
            return;
        }

        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_confirmingClose)
        {
            return;
        }

        CommitPendingEdits();
        _confirmingClose = true;
        try
        {
            if (await ViewModel.CanCloseAsync().ConfigureAwait(true))
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _appWindow.Closing -= AppWindow_Closing;
        _appWindow.Changed -= AppWindow_Changed;
        Root.SizeChanged -= Root_SizeChanged;
        Root.ActualThemeChanged -= Root_ActualThemeChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Session.PropertyChanged -= Session_PropertyChanged;
        SettingsSectionHost.Content = null;
        _sectionCache.Dispose();
        _dictionarySection = null;
        _readingSection = null;
        _securitySection = null;
        _dataSection = null;
        _aboutSection = null;
        Closed -= SettingsWindow_Closed;
    }

    private void ShowError(string message)
    {
        SaveError.Title = _localizer.Get("Common_OperationFailed");
        SaveError.Message = message;
        SaveError.IsOpen = true;
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == tag);
    }

    private bool BeginOperation(bool isMaintenance)
    {
        if (_operationCts is not null ||
            (isMaintenance && !ViewModel.TryBeginMaintenance()))
        {
            return false;
        }

        _maintenanceOperation = isMaintenance;
        _operationCts = new CancellationTokenSource();
        SavingProgress.IsActive = true;
        OperationProgressPanel.Visibility = Visibility.Visible;
        OperationProgressText.Text = _localizer.Get("Common_PreparingOperation");
        OperationProgressBar.IsIndeterminate = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        return true;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        SavingProgress.IsActive = false;
        OperationProgressPanel.Visibility = Visibility.Collapsed;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        if (_maintenanceOperation)
        {
            ViewModel.EndMaintenance();
            _maintenanceOperation = false;
        }

        if (_closeWhenSafe)
        {
            _closeWhenSafe = false;
            if (_discardChangesWhenSafe)
            {
                _discardChangesWhenSafe = false;
                ViewModel.Session.Cancel();
                ViewModel.AllowClose = true;
            }

            DispatcherQueue.TryEnqueue(Close);
        }
    }

    private void ApplyDraftAppearance()
    {
        ApplicationAppearance appearance = ViewModel.Session.Draft.Appearance;
        Root.RequestedTheme = appearance.Theme switch
        {
            CoreApplicationTheme.Light => ElementTheme.Light,
            CoreApplicationTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyTitleBarTheme();
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarTheme();

    private void ApplyTitleBarTheme() =>
        WindowThemeController.Apply(
            _appWindow.TitleBar,
            Root.ActualTheme == ElementTheme.Dark,
            transparentBackground: false);

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(
            WindowSizing.PixelsToDip(_hwnd, _appWindow.Size.Width));

    private void UpdateResponsiveLayout(double width)
    {
        bool narrow = width > 0 && width < 820;
        SettingsNavigation.OpenPaneLength = CalculateNavigationPaneWidth();
        SettingsNavigation.PaneDisplayMode = narrow
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.LeftCompact;
        SettingsSectionHost.Padding = narrow
            ? new Thickness(64, 16, 16, 16)
            : (Thickness)Application.Current.Resources["VDictPagePaddingThickness"];

        if (_dictionarySection is null)
        {
            OperationProgressPanel.Width = narrow ? 160 : 260;
            return;
        }

        if (narrow)
        {
            DictionaryLayout.ColumnSpacing = 0;
            DictionaryListColumn.Width = new GridLength(1, GridUnitType.Star);
            DictionaryLayout.ColumnDefinitions[1].Width = new GridLength(0);
            Grid.SetColumn(DictionaryEditorPanel, 0);
            Grid.SetRow(DictionaryEditorPanel, 1);
            DictionaryEditorPanel.MinWidth = 0;
            DictionaryList.MinHeight = 240;
            OperationProgressPanel.Width = 160;
        }
        else
        {
            DictionaryLayout.ColumnSpacing = 20;
            DictionaryListColumn.Width = new GridLength(
                Math.Clamp(width * 0.32, 280, 360));
            DictionaryLayout.ColumnDefinitions[1].Width =
                new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(DictionaryEditorPanel, 1);
            Grid.SetRow(DictionaryEditorPanel, 0);
            DictionaryEditorPanel.MinWidth = 280;
            DictionaryList.MinHeight = 300;
            OperationProgressPanel.Width = 260;
        }
    }

    private double CalculateNavigationPaneWidth()
    {
        double longestLabelWidth = SettingsNavigation.MenuItems
            .Concat(SettingsNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .Select(item => MeasureNavigationText(item.Content?.ToString(), 14))
            .DefaultIfEmpty(0)
            .Max();
        double titleWidth = MeasureNavigationText(SettingsPaneTitle.Text, 16);
        double desiredWidth = Math.Max(
            longestLabelWidth + 88,
            titleWidth + 70);
        return Math.Clamp(Math.Ceiling(desiredWidth), 220, 300);
    }

    private double MeasureNavigationText(string? text, double fontSize)
    {
        TextBlock probe = new()
        {
            FontFamily = SettingsPaneTitle.FontFamily,
            FontSize = fontSize,
            Text = text ?? string.Empty
        };
        probe.Measure(new Windows.Foundation.Size(
            double.PositiveInfinity,
            double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
            UpdateResponsiveLayout(
                WindowSizing.PixelsToDip(_hwnd, sender.Size.Width)));
        if (!args.DidSizeChange || _enforcingMinimumSize)
        {
            return;
        }

        _enforcingMinimumSize = true;
        try
        {
            WindowSizing.EnsureMinimumSize(sender, _hwnd, 760, 520);
        }
        finally
        {
            _enforcingMinimumSize = false;
        }
    }

    private void Session_PropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsEditSession.IsDirty) or
            nameof(SettingsEditSession.Validation))
        {
            UpdateSaveState();
        }
    }

    private void UpdateSaveState()
    {
        SaveButton.IsEnabled =
            ViewModel.CanEdit &&
            ViewModel.Session.IsDirty &&
            ViewModel.Session.Validation.IsValid;
        ToolTipService.SetToolTip(
            SaveButton,
            ViewModel.Session.IsDirty
                ? _localizer.Get("Settings_Save_TooltipDirty")
                : _localizer.Get("Settings_Save_TooltipClean"));
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsWindowViewModel.OperationProgress) &&
            ViewModel.OperationProgress is { } progress)
        {
            UpdateOperationProgress(progress);
        }

        if (e.PropertyName == nameof(SettingsWindowViewModel.State))
        {
            UpdateSaveState();
        }
    }

    private void UpdateOperationProgress(SettingsOperationProgress progress)
    {
        OperationProgressText.Text = progress.Message;
        OperationProgressBar.IsIndeterminate = progress.IsIndeterminate;
        if (!progress.IsIndeterminate)
        {
            OperationProgressBar.Maximum = Math.Max(1, progress.Total);
            OperationProgressBar.Value = Math.Clamp(progress.Completed, 0, progress.Total);
        }
    }
}
