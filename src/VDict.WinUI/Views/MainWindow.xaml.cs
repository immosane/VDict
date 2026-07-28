using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Presentation.ViewModels;
using VDict.WinUI.Controls;
using VDict.WinUI.Diagnostics;
using VDict.WinUI.Platform;
using Windows.System;
using WinRT.Interop;
using CoreApplicationTheme = VDict.Core.Models.ApplicationTheme;

namespace VDict.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly ReaderWebViewHost _readerHost;
    private readonly IDictionaryCatalog _catalog;
    private readonly DiagnosticsPrivacySettings _diagnosticsPrivacy;
    private readonly IAppLocalizer _localizer;
    private AppWindow? _appWindow;
    private nint _hwnd;
    private bool _enforcingMinimumSize;
    private bool _resizingCandidatePane;
    private bool _refreshingDictionaries;
    private double _candidatePaneDragOffset;
    private double? _preferredCandidatePaneWidth;
    private double? _pendingCandidatePaneWidth;
    private Task _candidatePanePersistenceTask = Task.CompletedTask;
    private long _dictionaryRebindCount;
    private readonly CandidateSelectionState _candidateSelection = new();
#if DEBUG
    private readonly DebugTimelineWindowController _debugTimeline;
    private readonly KeyboardAccelerator _debugTimelineAccelerator;
#endif

    public MainWindow(
        MainWindowViewModel viewModel,
        ReaderWebViewHost readerHost,
        IDictionaryCatalog catalog,
        WindowsCapabilities capabilities,
        MainWindowAccessor accessor,
        DiagnosticsPrivacySettings diagnosticsPrivacy,
        IAppLocalizer localizer
#if DEBUG
        ,
        DebugTimelineWindowController debugTimeline
#endif
        )
    {
        ViewModel = viewModel;
        _readerHost = readerHost;
        _catalog = catalog;
        _diagnosticsPrivacy = diagnosticsPrivacy;
        _localizer = localizer;
#if DEBUG
        _debugTimeline = debugTimeline;
#endif
        InitializeComponent();
#if DEBUG
        _debugTimelineAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.D,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        _debugTimelineAccelerator.Invoked += DebugTimelineAccelerator_Invoked;
        Root.KeyboardAccelerators.Add(_debugTimelineAccelerator);
#endif
        SearchBox.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(SearchBox_KeyDown),
            handledEventsToo: true);
        CandidateList.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(CandidateList_KeyDown),
            handledEventsToo: true);
        accessor.Window = this;
        ReaderHostContainer.Content = readerHost;
        ConfigureWindow(capabilities);
        ApplyAppearance(_catalog.GetSettingsSnapshot().Appearance);
        RefreshDictionaries();
        UpdateLookupPathBar();
        ViewModel.Reader.PropertyChanged += Reader_PropertyChanged;
        ViewModel.Reader.Candidates.CollectionChanged += Candidates_CollectionChanged;
        _catalog.SettingsCommitted += Catalog_SettingsCommitted;
        _readerHost.SelectionLookupRequested += ReaderHost_SelectionLookupRequested;
        _readerHost.StatusChanged += ReaderHost_StatusChanged;
        _readerHost.ZoomStepsRequested += ReaderHost_ZoomStepsRequested;
        _readerHost.ZoomResetRequested += ReaderHost_ZoomResetRequested;
        Root.Loaded += MainWindow_Loaded;
        Root.SizeChanged += Root_SizeChanged;
        Root.ActualThemeChanged += Root_ActualThemeChanged;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        _ = _readerHost.ShowDocumentAsync(ViewModel.Reader.Document);
    }

    public MainWindowViewModel ViewModel { get; }

    internal long DiagnosticDictionaryRebindCount =>
        Interlocked.Read(ref _dictionaryRebindCount);

    internal void ApplyInitializedSettings(ApplicationSettings settings)
    {
        _diagnosticsPrivacy.DetailedEnabled = settings.DetailedDiagnosticsEnabled;
        ApplyAppearance(settings.Appearance);
        RefreshDictionaries();
        UpdateResponsiveLayout(CurrentWindowWidthDip());
    }

    public async Task ShowFatalErrorAsync(string message)
    {
        using IDisposable webViewSuppression = _readerHost.SuppressForModal();
        ContentDialog dialog = new()
        {
            XamlRoot = Root.XamlRoot,
            Title = _localizer.Get("MainWindow_StartupFailed_Title"),
            Content = message,
            CloseButtonText = _localizer.Get("Common_Exit"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void ConfigureWindow(WindowsCapabilities capabilities)
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (capabilities.CanUseMica)
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        WindowIcon.Apply(appWindow);
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        WindowSizing.ResizeToWorkArea(appWindow, _hwnd, 1180, 760, 820, 520);
        WindowThemeController.Apply(
            appWindow.TitleBar,
            Root.ActualTheme == ElementTheme.Dark,
            transparentBackground: true);
        appWindow.Changed += AppWindow_Changed;
    }

    private void RefreshDictionaries()
    {
        Interlocked.Increment(ref _dictionaryRebindCount);
        _refreshingDictionaries = true;
        try
        {
            ViewModel.Reader.RefreshDictionarySelection();
            DictionaryId? selected = ViewModel.Reader.SelectedDictionaryId;
            List<DictionaryOption> options = _catalog.GetSnapshot().ActiveDictionaries
                .Select(dictionary =>
                    new DictionaryOption(dictionary.Id, dictionary.EffectiveDisplayName))
                .ToList();
            DictionarySelector.ItemsSource = options;
            DictionarySelector.SelectedItem =
                options.FirstOrDefault(option => option.Id == selected);
        }
        finally
        {
            _refreshingDictionaries = false;
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        using IDisposable interaction = DebugLookupProbe.BeginInteraction(
            "SearchButton",
            $"QueryLength={SearchBox.Text.Trim().Length}");
#endif
        _ = ViewModel.Reader.LookupAsync(SearchBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Down or VirtualKey.Up && CandidateList.Items.Count > 0)
        {
            int delta = e.Key == VirtualKey.Down ? 1 : -1;
            MoveCandidateSelection(delta, CandidateSelectionOrigin.Keyboard);
            CandidateList.ScrollIntoView(CandidateList.SelectedItem);
            SearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            QuerySelectedCandidate();
            e.Handled = true;
        }
    }

    private void CandidateList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string word)
        {
#if DEBUG
            using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                "CandidateClick",
                $"QueryLength={word.Trim().Length}");
#endif
            _candidateSelection.Mark(word, CandidateSelectionOrigin.Pointer);
            _ = ViewModel.Reader.LookupCandidateAsync(word);
        }
    }

    private void CandidateList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Down or VirtualKey.Up && CandidateList.Items.Count > 0)
        {
            MoveCandidateSelection(
                e.Key == VirtualKey.Down ? 1 : -1,
                CandidateSelectionOrigin.Keyboard);
            CandidateList.ScrollIntoView(CandidateList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            QuerySelectedCandidate();
            e.Handled = true;
        }
    }

    private void QuerySelectedCandidate()
    {
        string query = _candidateSelection.ResolveEnterQuery(SearchBox.Text);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

#if DEBUG
        using IDisposable interaction = DebugLookupProbe.BeginInteraction(
            _candidateSelection.Origin == CandidateSelectionOrigin.None
                ? "SearchEnter"
                : "CandidateEnter",
            $"QueryLength={query.Trim().Length}");
#endif
        _ = _candidateSelection.Origin == CandidateSelectionOrigin.None
            ? ViewModel.Reader.LookupAsync(query)
            : ViewModel.Reader.LookupCandidateAsync(query);
    }

    private void MoveCandidateSelection(int delta, CandidateSelectionOrigin origin)
    {
        int count = CandidateList.Items.Count;
        if (count == 0)
        {
            return;
        }

        int next = CandidateSelectionState.MoveIndex(
            CandidateList.SelectedIndex,
            count,
            delta);
        CandidateList.SelectedIndex = next;
        _candidateSelection.Mark(CandidateList.SelectedItem as string, origin);
    }

    private void DictionarySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DictionarySelector.SelectedItem is DictionaryOption option)
        {
            ViewModel.Reader.SelectedDictionaryId = option.Id;
            if (!_refreshingDictionaries &&
                !string.IsNullOrWhiteSpace(ViewModel.Reader.CurrentWord))
            {
#if DEBUG
                using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                    "DictionaryScopeChange",
                    $"QueryLength={ViewModel.Reader.CurrentWord.Trim().Length}");
#endif
                _ = ViewModel.Reader.LookupAsync(
                    ViewModel.Reader.CurrentWord,
                    recordHistory: false,
                    origin: LookupOrigin.ScopeRefresh);
            }
        }
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reader.SetZoom(ViewModel.Reader.Zoom - 0.1);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reader.SetZoom(ViewModel.Reader.Zoom + 0.1);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Reader.SetZoom(1.0);
    }

    private void Reader_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.LookupPathItems))
        {
            UpdateLookupPathBar();
        }

        if (e.PropertyName == nameof(ReaderViewModel.Document))
        {
            _ = _readerHost.ShowDocumentAsync(ViewModel.Reader.Document);
            SearchBox.Focus(FocusState.Programmatic);
        }

        if (e.PropertyName == nameof(ReaderViewModel.Zoom))
        {
            _readerHost.SetZoom(ViewModel.Reader.Zoom);
        }
    }

    private void Catalog_SettingsCommitted(object? sender, SettingsCommittedEvent e)
    {
        _diagnosticsPrivacy.DetailedEnabled =
            _catalog.GetSettingsSnapshot().DetailedDiagnosticsEnabled;
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            ApplicationSettings settings = _catalog.GetSettingsSnapshot();
            if (e.Changes.Has(SettingsChangeKind.Zoom))
            {
                ViewModel.Reader.ApplyCommittedZoom(settings.Appearance.ContentZoom);
            }

            if (e.Changes.Has(SettingsChangeKind.MainWindowLayout))
            {
                _preferredCandidatePaneWidth = settings.Appearance.CandidatePaneWidth;
                UpdateResponsiveLayout(CurrentWindowWidthDip());
            }

            if (e.Changes.Has(SettingsChangeKind.ApplicationTheme) ||
                e.Changes.Has(SettingsChangeKind.ReaderAppearance))
            {
                ApplyAppearance(settings.Appearance);
            }

            bool catalogChanged = e.Changes.RequiresCatalogReload;
            bool presentationChanged =
                e.Changes.Has(SettingsChangeKind.DictionaryPresentation);
            if (catalogChanged || presentationChanged)
            {
                RefreshDictionaries();
            }

            bool readerNeedsRefresh =
                catalogChanged ||
                presentationChanged ||
                e.Changes.Has(SettingsChangeKind.ReaderAppearance) ||
                (e.Changes.Has(SettingsChangeKind.ApplicationTheme) &&
                 !_readerHost.DynamicThemeEnabled);
            if (readerNeedsRefresh)
            {
                string currentWord = ViewModel.Reader.CurrentWord;
                if (!string.IsNullOrWhiteSpace(currentWord))
                {
                    await ViewModel.Reader.LookupAsync(
                        currentWord,
                        recordHistory: false,
                        origin: LookupOrigin.ScopeRefresh).ConfigureAwait(true);
                }
                else
                {
                    ViewModel.Reader.RefreshWelcome();
                }
            }
        });
    }

    private void ReaderHost_SelectionLookupRequested(object? sender, string word)
    {
        _ = ViewModel.Reader.NavigateSelectedTextAsync(word);
    }

    private void UpdateLookupPathBar()
    {
        LookupPathPanel.Children.Clear();
        foreach (LookupPathItem item in ViewModel.Reader.LookupPathItems)
        {
            if (item.Index > 0)
            {
                FontIcon separator = new()
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 10,
                    Glyph = "\uE76C",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 2, 0),
                    IsHitTestVisible = false
                };
                LookupPathPanel.Children.Add(separator);
            }

            TextBlock label = new()
            {
                Text = item.Word,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (item.IsCurrent)
            {
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                label.Margin = new Thickness(8, 2, 8, 2);
                AutomationProperties.SetName(label, item.AccessibleName);
                AutomationProperties.SetHelpText(
                    label,
                    _localizer.Get("MainWindow_CurrentPath"));
                ToolTipService.SetToolTip(label, item.Word);
                LookupPathPanel.Children.Add(new Border
                {
                    Background =
                        Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
                    CornerRadius = new CornerRadius(4),
                    Child = label
                });
                continue;
            }

            Button button = new()
            {
                Content = label,
                Tag = item.Index,
                MinHeight = 24,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4)
            };
            AutomationProperties.SetName(button, item.AccessibleName);
            ToolTipService.SetToolTip(button, item.Word);
            button.Click += LookupPathButton_Click;
            LookupPathPanel.Children.Add(button);
        }
    }

    private void Candidates_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        CandidateList.SelectedIndex = -1;
        _candidateSelection.Reset();
    }

    private void LookupPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
        {
#if DEBUG
            using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                "LookupPathClick",
                $"PathIndex={index}");
#endif
            _ = ViewModel.Reader.NavigateLookupPathAsync(index);
        }
    }

    private void ReaderHost_StatusChanged(object? sender, string message) =>
        DispatcherQueue.TryEnqueue(() => ViewModel.Reader.StatusMessage = message);

    private void ReaderHost_ZoomStepsRequested(int steps) =>
        DispatcherQueue.TryEnqueue(() =>
            ViewModel.Reader.SetZoom(ViewModel.Reader.Zoom + steps * 0.1));

    private void ReaderHost_ZoomResetRequested() =>
        DispatcherQueue.TryEnqueue(() => ViewModel.Reader.SetZoom(1.0));

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
#if DEBUG
        _debugTimeline.Close();
        _debugTimelineAccelerator.Invoked -= DebugTimelineAccelerator_Invoked;
        Root.KeyboardAccelerators.Remove(_debugTimelineAccelerator);
#endif
        Root.Loaded -= MainWindow_Loaded;
        Root.SizeChanged -= Root_SizeChanged;
        Root.ActualThemeChanged -= Root_ActualThemeChanged;
        Activated -= MainWindow_Activated;
        if (_appWindow is { } appWindow)
        {
            appWindow.Changed -= AppWindow_Changed;
        }
        ViewModel.Reader.PropertyChanged -= Reader_PropertyChanged;
        ViewModel.Reader.Candidates.CollectionChanged -= Candidates_CollectionChanged;
        SearchBox.RemoveHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(SearchBox_KeyDown));
        CandidateList.RemoveHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(CandidateList_KeyDown));
        _catalog.SettingsCommitted -= Catalog_SettingsCommitted;
        _readerHost.SelectionLookupRequested -= ReaderHost_SelectionLookupRequested;
        _readerHost.StatusChanged -= ReaderHost_StatusChanged;
        _readerHost.ZoomStepsRequested -= ReaderHost_ZoomStepsRequested;
        _readerHost.ZoomResetRequested -= ReaderHost_ZoomResetRequested;
        _readerHost.Dispose();
        ViewModel.Reader.Dispose();
        Closed -= MainWindow_Closed;
    }

#if DEBUG
    private void DebugTimelineAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        _debugTimeline.Toggle(this);
        args.Handled = true;
    }
#endif

    private void ApplyAppearance(ApplicationAppearance appearance)
    {
        _preferredCandidatePaneWidth = appearance.CandidatePaneWidth;
        Root.RequestedTheme = appearance.Theme switch
        {
            VDict.Core.Models.ApplicationTheme.Light => ElementTheme.Light,
            VDict.Core.Models.ApplicationTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        ApplyResolvedTheme(appearance.Theme);
        ViewModel.Reader.ApplyCommittedZoom(appearance.ContentZoom);
        _readerHost.SetZoom(ViewModel.Reader.Zoom);
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args)
    {
        CoreApplicationTheme theme = _catalog.GetSettingsSnapshot().Appearance.Theme;
        if (theme == CoreApplicationTheme.System)
        {
            ApplyResolvedTheme(theme);
        }
    }

    private void ApplyResolvedTheme(CoreApplicationTheme theme)
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        if (_appWindow is { } appWindow)
        {
            WindowThemeController.Apply(
                appWindow.TitleBar,
                dark,
                transparentBackground: true);
        }

        _ = _readerHost.ApplyThemeAsync(theme, dark);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout(CurrentWindowWidthDip());
        if (_appWindow is { } appWindow)
        {
            UpdateTitleBarInsets(appWindow.TitleBar);
        }
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _readerHost.SynchronizeWithOwnerWindow(notifyParentPositionChanged: true);
        }

        if (_appWindow is { } appWindow)
        {
            UpdateTitleBarInsets(appWindow.TitleBar);
        }
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(CurrentWindowWidthDip());

    private double CurrentWindowWidthDip() =>
        _appWindow is { } appWindow
            ? Root.ActualWidth > 0
                ? Math.Min(
                    Root.ActualWidth,
                    WindowSizing.PixelsToDip(_hwnd, appWindow.Size.Width))
                : WindowSizing.PixelsToDip(_hwnd, appWindow.Size.Width)
            : Root.ActualWidth;

    private void UpdateResponsiveLayout(double width)
    {
        ProductTitle.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        ForwardButton.Visibility = Visibility.Visible;
        DictionarySelector.Visibility = Visibility.Visible;
        DictionarySelector.Width = double.NaN;
        CandidatePane.Visibility = Visibility.Visible;
        CandidateDivider.Visibility = Visibility.Visible;
        CandidateDividerColumn.Width = new GridLength(8);
        double contentWidth = ContentLayoutGrid.ActualWidth > 0
            ? ContentLayoutGrid.ActualWidth
            : Math.Max(
                0,
                width - ContentLayoutGrid.Margin.Left - ContentLayoutGrid.Margin.Right);
        CandidateColumn.Width = new GridLength(
            CandidatePaneLayoutPolicy.ResolveDisplayedWidth(
                _preferredCandidatePaneWidth,
                width,
                contentWidth,
                CandidateDividerColumn.Width.Value,
                ReaderColumn.MinWidth));
        LookupPathScroller.Visibility = Visibility.Visible;
    }

    private void UpdateTitleBarInsets(AppWindowTitleBar titleBar)
    {
        double rightInset = SafeTitleBarInsetDip((int)titleBar.RightInset);
        double leftInset = SafeTitleBarInsetDip((int)titleBar.LeftInset);
        CaptionButtonInsetColumn.Width = new GridLength(rightInset);
        AppTitleBar.Padding = new Thickness(
            leftInset + 16,
            0,
            8,
            0);
    }

    private double SafeTitleBarInsetDip(int insetPixels)
    {
        int windowWidth = _appWindow?.Size.Width ?? 0;
        if (insetPixels < 0 || windowWidth <= 0 || insetPixels > windowWidth)
        {
            return 0;
        }

        double inset = WindowSizing.PixelsToDip(_hwnd, insetPixels);
        return double.IsFinite(inset) && inset >= 0 ? inset : 0;
    }

    private void CandidateDivider_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        _candidatePaneDragOffset =
            e.GetCurrentPoint(CandidateDivider).Position.X;
        _resizingCandidatePane = CandidateDivider.CapturePointer(e.Pointer);
        CandidateDivider.IsDragging = _resizingCandidatePane;
        e.Handled = _resizingCandidatePane;
    }

    private void CandidateDivider_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        double maximum = CandidatePaneLayoutPolicy.GetMaximumDisplayedWidth(
            ContentLayoutGrid.ActualWidth,
            CandidateDivider.ActualWidth,
            ReaderColumn.MinWidth);
        double current = CandidateColumn.ActualWidth > 0
            ? CandidateColumn.ActualWidth
            : CandidateColumn.Width.Value;
        double delta = e.Key == VirtualKey.Left ? -16 : 16;
        double adjusted = Math.Clamp(
            current + delta,
            CandidatePaneLayoutPolicy.MinimumWidth,
            maximum);
        _preferredCandidatePaneWidth = adjusted;
        CandidateColumn.Width = new GridLength(adjusted);
        QueueCandidatePaneWidthPersistence(adjusted);
        e.Handled = true;
    }

    private void CandidateDivider_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_resizingCandidatePane)
        {
            return;
        }

        double maximum = CandidatePaneLayoutPolicy.GetMaximumDisplayedWidth(
            ContentLayoutGrid.ActualWidth,
            CandidateDivider.ActualWidth,
            ReaderColumn.MinWidth);
        double requested =
            e.GetCurrentPoint(ContentLayoutGrid).Position.X -
            _candidatePaneDragOffset;
        double adjusted = Math.Clamp(
            requested,
            CandidatePaneLayoutPolicy.MinimumWidth,
            maximum);
        _preferredCandidatePaneWidth = adjusted;
        CandidateColumn.Width = new GridLength(adjusted);
        e.Handled = true;
    }

    private void CandidateDivider_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _resizingCandidatePane = false;
        CandidateDivider.IsDragging = false;
        CandidateDivider.ReleasePointerCapture(e.Pointer);
        QueueCandidatePaneWidthPersistence(CandidateColumn.Width.Value);
        e.Handled = true;
    }

    private void CandidateDivider_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        bool wasResizing = _resizingCandidatePane;
        _resizingCandidatePane = false;
        CandidateDivider.IsDragging = false;
        if (wasResizing)
        {
            QueueCandidatePaneWidthPersistence(CandidateColumn.Width.Value);
        }
    }

    internal Task FlushCandidatePaneWidthAsync() => _candidatePanePersistenceTask;

    private void QueueCandidatePaneWidthPersistence(double width)
    {
        _pendingCandidatePaneWidth = Math.Round(
            Math.Clamp(
                width,
                CandidatePaneLayoutPolicy.MinimumWidth,
                CandidatePaneLayoutPolicy.MaximumWidth),
            0);
        if (_candidatePanePersistenceTask.IsCompleted)
        {
            _candidatePanePersistenceTask = PersistCandidatePaneWidthAsync();
        }
    }

    private async Task PersistCandidatePaneWidthAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
        while (_pendingCandidatePaneWidth is { } width)
        {
            _pendingCandidatePaneWidth = null;
            ApplicationSettings settings = _catalog.GetSettingsSnapshot();
            SettingsCommitResult result = await _catalog.UpdateCandidatePaneWidthAsync(
                width,
                settings.Revision,
                settings.Appearance.CandidatePaneWidth).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ViewModel.Reader.StatusMessage =
                    result.Error ?? _localizer.Get("MainWindow_CandidatePaneSaveFailed");
            }
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
            UpdateResponsiveLayout(
                Root.ActualWidth > 0
                    ? Math.Min(
                        Root.ActualWidth,
                        WindowSizing.PixelsToDip(_hwnd, sender.Size.Width))
                    : WindowSizing.PixelsToDip(_hwnd, sender.Size.Width)));
        if (!args.DidSizeChange || _enforcingMinimumSize)
        {
            return;
        }

        _enforcingMinimumSize = true;
        try
        {
            WindowSizing.EnsureMinimumSize(sender, _hwnd, 820, 520);
        }
        finally
        {
            _enforcingMinimumSize = false;
        }
    }

    private sealed record DictionaryOption(DictionaryId Id, string Name)
    {
        public override string ToString() => Name;
    }
}
