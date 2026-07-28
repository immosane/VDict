using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Core.Text;
using VDict.Presentation.Services;
using VDict.WinUI.Platform;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;
using CoreApplicationTheme = VDict.Core.Models.ApplicationTheme;

namespace VDict.WinUI.Controls;

internal enum ReaderWebViewLifecycleState
{
    NotStarted,
    Initializing,
    Ready,
    Failed,
    Recovering,
    Disposed
}

public sealed partial class ReaderWebViewHost : UserControl, IDisposable
{
    private static readonly Uri RuntimeHelpUri =
        new("https://developer.microsoft.com/microsoft-edge/webview2/");
    private static readonly Lazy<ReaderDarkModeAssets?> DarkModeAssets =
        new(LoadDarkModeAssets);
    private static readonly Lazy<byte[]?> WelcomeLogo = new(LoadWelcomeLogo);
    private readonly IDictionaryCatalog _catalog;
    private readonly IAppDataPaths _paths;
    private readonly IExternalLinkService _externalLinks;
    private readonly IAppLogger _logger;
    private readonly IAppLocalizer _localizer;
    private readonly MainWindowAccessor _mainWindowAccessor;
    private readonly bool _backgroundFrameBuildEnabled;
    private readonly bool _dynamicThemeEnabled;
    private long _renderStartedTimestamp;
    private readonly ReaderWebResourceAdapter _resourceAdapter;
    private readonly TrustedWebActivationGate _activationGate = new();
    private readonly ReaderNavigationState _navigationState = new();
    private readonly ZoomDeltaAccumulator _zoomAccumulator = new();
    private readonly List<CoreWebView2Frame> _frames = [];
    private readonly Dictionary<CoreWebView2Frame, string> _frameCorrelations = [];
    private readonly HashSet<string> _readyFrameIds = new(StringComparer.Ordinal);
    private readonly object _initializationSync = new();
    private readonly object _zoomSync = new();
    private readonly object _frameBuildSync = new();
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly SemaphoreSlim _themeGate = new(1, 1);
    private readonly SemaphoreSlim _sideEffectGate = new(1, 1);
    private readonly ConcurrentExclusiveSchedulerPair _frameBuildScheduler =
        new(TaskScheduler.Default, maxConcurrencyLevel: 1);
    private readonly Dictionary<string, Task<ReaderFrameBuildResult>> _frameBuildTasks = [];
    private CancellationTokenSource _frameBuildCancellation = new();
    private XamlRoot? _observedXamlRoot;
    private AppWindow? _parentAppWindow;
    private ReaderDocument? _pendingDocument;
    private ReaderDocument? _document;
    private Task? _initializationTask;
    private int _previewCaptured;
    private int _frameSequence;
    private bool _eventsAttached;
    private bool _contentVisibleRequested;
    private int _visibilitySuppressionCount;
    private Windows.Foundation.Rect? _lastControllerBounds;
    private double _lastRasterizationScale;
    private ReaderWebViewLifecycleState _lifecycleState = ReaderWebViewLifecycleState.NotStarted;
    private CoreWebView2Environment? _environment;
    private CoreWebView2ContextMenuItem? _selectionLookupMenuItem;
    private readonly ContextSelectionState _contextSelection = new();
    private double _requestedZoom = 1.0;
    private long _zoomVersion;
    private bool _zoomApplyRunning;
    private TaskCompletionSource<ReaderRenderCompletion>? _renderCompletion;
    private string _renderCompletionToken = string.Empty;
    private double _firstFrameReadyMilliseconds;
    private long _navigationSubmitCount;
    private long _scriptExecutionCount;
    private bool _effectiveApplicationDark;
    private bool _themeInitialized;

    private CoreWebView2? Core => BrowserSurface.CoreWebView2;

    public ReaderWebViewHost(
        IDictionaryCatalog catalog,
        IAppDataPaths paths,
        IExternalLinkService externalLinks,
        IAppLogger logger,
        MainWindowAccessor mainWindowAccessor,
        IAppLocalizer localizer)
    {
        _catalog = catalog;
        _paths = paths;
        _externalLinks = externalLinks;
        _logger = logger;
        _localizer = localizer;
        _mainWindowAccessor = mainWindowAccessor;
        _backgroundFrameBuildEnabled = !string.Equals(
            System.Environment.GetEnvironmentVariable(
                "VDICT_DISABLE_BACKGROUND_RENDER_PIPELINE"),
            "1",
            StringComparison.Ordinal);
        _dynamicThemeEnabled = !string.Equals(
            System.Environment.GetEnvironmentVariable(
                "VDICT_DISABLE_DYNAMIC_THEME"),
            "1",
            StringComparison.Ordinal);
        _resourceAdapter = new ReaderWebResourceAdapter(catalog);
        InitializeComponent();
        Loaded += ReaderWebViewHost_Loaded;
        Unloaded += ReaderWebViewHost_Unloaded;
        SizeChanged += ReaderWebViewHost_SizeChanged;
        LayoutUpdated += ReaderWebViewHost_LayoutUpdated;
    }

    public event EventHandler<string>? SelectionLookupRequested;

    public event EventHandler<string>? StatusChanged;

    public event Action<int>? ZoomStepsRequested;

    public event Action? ZoomResetRequested;

    internal long DiagnosticNavigationSubmitCount =>
        Interlocked.Read(ref _navigationSubmitCount);

    internal long DiagnosticScriptExecutionCount =>
        Interlocked.Read(ref _scriptExecutionCount);

    internal bool DynamicThemeEnabled => _dynamicThemeEnabled;

    internal string WebViewRuntimeVersion =>
        _environment?.BrowserVersionString ??
        CoreWebView2Environment.GetAvailableBrowserVersionString();

    public IDisposable SuppressForModal()
    {
        _visibilitySuppressionCount++;
        UpdateControllerVisibility();
        return new VisibilitySuppression(this);
    }

    public void SynchronizeWithOwnerWindow(bool notifyParentPositionChanged = false)
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        AttachLayoutSignals();
        UpdateControllerLayout(
            notifyParentPositionChanged,
            forceVisibility: true);
        ApplyZoom();
        if (_document is { } document)
        {
            _ = ApplyThemeAsync(document.Appearance.Theme);
        }
    }

    internal async Task TriggerBrowserProcessFailureForDiagnosticsAsync()
    {
        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            throw new InvalidOperationException(
                "Compatibility diagnostics must be enabled for the browser recovery probe.");
        }

        CoreWebView2 core = Core ?? throw new InvalidOperationException(
            "WebView2 is not ready for the browser recovery probe.");
        _logger.Write(
            AppLogLevel.Warning,
            "webview-process-probe",
            $"Render={RenderCorrelation(_document)}; BrowserCrashRequested=true");
        try
        {
            _ = await core.CallDevToolsProtocolMethodAsync("Browser.crash", "{}");
        }
        catch (COMException)
        {
            // The browser process terminates before the protocol call can return.
        }
    }

    public async Task ShowDocumentAsync(
        ReaderDocument? document,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _pendingDocument = document;
            long version = _navigationState.RegisterDocument();
            DebugLookupProbe.Hit(
                _logger,
                "ShowDocumentReceived",
                renderToken: document?.RenderToken,
                detail: $"Version={version}");
            if (!HasUsableLayout())
            {
                return;
            }

            await RenderDocumentAsync(document, version, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Error,
                "webview-navigation",
                "词典正文呈现失败。",
                exception);
            ShowContentError();
        }
    }

    public void SetZoom(double zoom)
    {
        lock (_zoomSync)
        {
            _requestedZoom = Math.Clamp(zoom, 0.5, 2.5);
            _zoomVersion++;
            if (_lifecycleState != ReaderWebViewLifecycleState.Ready ||
                _zoomApplyRunning)
            {
                return;
            }

            _zoomApplyRunning = true;
        }

        _ = ApplyZoomLoopAsync();
    }

    public async Task PrepareForCacheMaintenanceAsync()
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        Task drainRequests = _resourceAdapter.BeginMaintenanceAsync();
        await StopMediaAsync().ConfigureAwait(true);
        await drainRequests.ConfigureAwait(true);
    }

    public async Task ClearBrowsingDataAsync()
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        await PrepareForCacheMaintenanceAsync().ConfigureAwait(true);
        try
        {
            CoreWebView2 core = Core ?? throw new InvalidOperationException(
                "WebView2 尚未就绪，无法清理浏览数据。");
            await core.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.AllDomStorage |
                    CoreWebView2BrowsingDataKinds.ServiceWorkers);
        }
        finally
        {
            _resourceAdapter.BeginDocument(_document);
        }
    }

    public void ResumeAfterCacheMaintenance()
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        // Use the current catalog generation. A settings-level maintenance caller
        // follows this with a fresh lookup or welcome document.
        _resourceAdapter.BeginDocument();
    }

    public void Dispose()
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        _lifecycleState = ReaderWebViewLifecycleState.Disposed;
        DetachLayoutSignals();
        Loaded -= ReaderWebViewHost_Loaded;
        Unloaded -= ReaderWebViewHost_Unloaded;
        SizeChanged -= ReaderWebViewHost_SizeChanged;
        LayoutUpdated -= ReaderWebViewHost_LayoutUpdated;
        _frameBuildScheduler.Complete();
        CancellationTokenSource frameBuildCancellation;
        Task<ReaderFrameBuildResult>[] frameBuilds;
        lock (_frameBuildSync)
        {
            frameBuildCancellation = _frameBuildCancellation;
            frameBuilds = _frameBuildTasks.Values.ToArray();
            _frameBuildTasks.Clear();
        }
        frameBuildCancellation.Cancel();
        _ = DisposeFrameBuildSessionAsync(frameBuildCancellation, frameBuilds);
        CloseController();
        _activationGate.Revoke();
        _sideEffectGate.Dispose();
        _resourceAdapter.Dispose();
    }

    private void ReaderWebViewHost_Loaded(object sender, RoutedEventArgs e)
    {
        AttachLayoutSignals();
        UpdateControllerLayout();
        if (_lifecycleState != ReaderWebViewLifecycleState.Disposed && HasUsableLayout())
        {
            _ = InitializeFromEventAsync();
        }
    }

    private void ReaderWebViewHost_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachLayoutSignals();
        UpdateControllerVisibility();
    }

    private void ReaderWebViewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateControllerLayout();
        if (_lifecycleState != ReaderWebViewLifecycleState.Disposed && HasUsableLayout())
        {
            _ = InitializeFromEventAsync();
        }
    }

    private void ReaderWebViewHost_LayoutUpdated(object? sender, object e) =>
        UpdateControllerLayout();

    private void AttachLayoutSignals()
    {
        XamlRoot? xamlRoot = XamlRoot;
        if (!ReferenceEquals(_observedXamlRoot, xamlRoot))
        {
            if (_observedXamlRoot is not null)
            {
                _observedXamlRoot.Changed -= XamlRoot_Changed;
            }

            _observedXamlRoot = xamlRoot;
            if (_observedXamlRoot is not null)
            {
                _observedXamlRoot.Changed += XamlRoot_Changed;
            }
        }

        if (_parentAppWindow is null && _mainWindowAccessor.Window is { } owner)
        {
            nint hwnd = WindowNative.GetWindowHandle(owner);
            if (hwnd != 0)
            {
                Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _parentAppWindow = AppWindow.GetFromWindowId(windowId);
                _parentAppWindow.Changed += ParentAppWindow_Changed;
            }
        }
    }

    private void DetachLayoutSignals()
    {
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed -= XamlRoot_Changed;
            _observedXamlRoot = null;
        }

        if (_parentAppWindow is not null)
        {
            _parentAppWindow.Changed -= ParentAppWindow_Changed;
            _parentAppWindow = null;
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdateControllerLayout();

    private void ParentAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => UpdateControllerLayout(
                notifyParentPositionChanged: args.DidPositionChange));
    }

    private bool HasUsableLayout() =>
        IsLoaded &&
        Visibility == Visibility.Visible &&
        ReaderSurface.Visibility == Visibility.Visible &&
        XamlRoot is not null &&
        ActualWidth > 0 &&
        ActualHeight > 0 &&
        Root.ActualWidth > 0 &&
        Root.ActualHeight > 0 &&
        ReaderSurface.ActualWidth > 0 &&
        ReaderSurface.ActualHeight > 0;

    private async Task InitializeFromEventAsync()
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed || !HasUsableLayout())
        {
            return;
        }

        try
        {
            await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(true);
            ReaderDocument? pending = _pendingDocument;
            long version = _navigationState.LatestVersion;
            if (pending is not null && _navigationState.ShouldRender(version))
            {
                await RenderDocumentAsync(pending, version, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Error, "webview-resource", "资源加载失败。", exception);
            ShowRuntimeError();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            return;
        }

        Task initialization;
        lock (_initializationSync)
        {
            if (_lifecycleState == ReaderWebViewLifecycleState.Ready)
            {
                return;
            }

            if (_initializationTask is null)
            {
                _lifecycleState = ReaderWebViewLifecycleState.Initializing;
                _initializationTask = InitializeCoreAsync();
            }

            initialization = _initializationTask;
        }

        WriteInitializationDiagnostics("ensure-start");
        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(true);
            WriteInitializationDiagnostics("ensure-complete");
        }
        finally
        {
            if (_lifecycleState == ReaderWebViewLifecycleState.Failed && initialization.IsCompleted)
            {
                lock (_initializationSync)
                {
                    if (ReferenceEquals(_initializationTask, initialization))
                    {
                        _initializationTask = null;
                    }
                }
            }
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
            {
                return;
            }

            if (Core is { } existingCore)
            {
                _environment ??= existingCore.Environment;
                ConfigureCore(existingCore);
                RuntimeError.Visibility = Visibility.Collapsed;
                SetControllerVisible(true);
                _lifecycleState = ReaderWebViewLifecycleState.Ready;
                return;
            }

            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new WebView2RuntimeNotFoundException();
            }

            // Check minimum WebView2 version (120.0.2210.0 for TreatAsSecure support)
            if (Version.TryParse(version.Split(' ')[0], out Version? installedVersion) &&
                installedVersion < new Version(120, 0, 2210, 0))
            {
                throw new WebView2RuntimeNotFoundException(
                    $"WebView2 Runtime 版本 {version} 过低，需要 120.0.2210.0 或更高版本。");
            }

            _paths.EnsureCreated();
            CoreWebView2CustomSchemeRegistration resourceScheme = new("vdict")
            {
                HasAuthorityComponent = true,
                TreatAsSecure = 1
            };
            resourceScheme.AllowedOrigins.Add("https://*.vdict.invalid");
            resourceScheme.AllowedOrigins.Add("vdict-doc://*.vdict.invalid");
            CoreWebView2CustomSchemeRegistration documentScheme = new(
                DictionaryNetworkPolicy.InternalScheme)
            {
                HasAuthorityComponent = true,
                TreatAsSecure = 1
            };
            documentScheme.AllowedOrigins.Add("vdict-doc://*.vdict.invalid");
            CoreWebView2EnvironmentOptions options = new()
            {
                CustomSchemeRegistrations = [resourceScheme, documentScheme]
            };
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    _paths.WebView2Directory,
                    options);
            if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
            {
                return;
            }

            _environment = environment;
            CoreWebView2ControllerOptions controllerOptions =
                environment.CreateCoreWebView2ControllerOptions();
            await BrowserSurface.EnsureCoreWebView2Async(environment, controllerOptions);
            if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
            {
                BrowserSurface.Close();
                return;
            }

            CoreWebView2 core = BrowserSurface.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 initialization completed without a core instance.");
            ConfigureCore(core);
            _logger.Write(
                AppLogLevel.Information,
                "webview-baseline",
                $"Schema=reader-baseline-v1; Stage=RuntimeReady; " +
                $"WebView2Runtime={environment.BrowserVersionString}; " +
                "UserDataIsolated=true");
            RuntimeError.Visibility = Visibility.Collapsed;
            SetControllerVisible(true);
            _lifecycleState = ReaderWebViewLifecycleState.Ready;
        }
        catch (Exception exception)
            when (exception is WebView2RuntimeNotFoundException or
                  COMException or InvalidOperationException)
        {
            if (_lifecycleState != ReaderWebViewLifecycleState.Disposed)
            {
                _lifecycleState = ReaderWebViewLifecycleState.Failed;
                _logger.Write(
                    AppLogLevel.Error,
                    "webview-initialization",
                    "WebView2 runtime initialization failed.",
                    exception);
                ShowRuntimeError();
            }
        }
    }

    private async Task RenderDocumentAsync(
        ReaderDocument? document,
        long version,
        CancellationToken cancellationToken)
    {
        WriteRenderDiagnostics("render-enter", document, version);
        if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
        {
            WriteRenderDiagnostics("render-skip", document, version, "disposed");
            return;
        }
        if (!HasUsableLayout())
        {
            WriteRenderDiagnostics("render-skip", document, version, "layout-unusable");
            return;
        }

        WriteRenderDiagnostics("render-before-initialize", document, version);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(true);
        WriteRenderDiagnostics("render-after-initialize", document, version);
        if (_lifecycleState != ReaderWebViewLifecycleState.Ready)
        {
            WriteRenderDiagnostics("render-skip", document, version, "lifecycle-not-ready");
            return;
        }
        if (document is null)
        {
            WriteRenderDiagnostics("render-skip", document, version, "document-null");
            return;
        }
        if (!_navigationState.ShouldRender(version))
        {
            WriteRenderDiagnostics("render-skip", document, version, "stale-before-gate");
            return;
        }

        _ = await ReaderRenderPipeline.ExecuteAsync(
            version,
            _navigationState,
            _navigationGate,
            () => _lifecycleState == ReaderWebViewLifecycleState.Ready && Core is not null,
            StopMediaAsync,
            () =>
            {
                CoreWebView2 core = Core ?? throw new InvalidOperationException(
                    "WebView2 core disappeared while submitting reader navigation.");
                _activationGate.Revoke();
                _document = document;
                CancellationTokenSource retiredCancellation;
                Task<ReaderFrameBuildResult>[] retiredBuilds;
                lock (_frameBuildSync)
                {
                    retiredCancellation = _frameBuildCancellation;
                    retiredBuilds = _frameBuildTasks.Values.ToArray();
                    _frameBuildCancellation = new CancellationTokenSource();
                    _frameBuildTasks.Clear();
                }
                retiredCancellation.Cancel();
                _ = DisposeFrameBuildSessionAsync(
                    retiredCancellation,
                    retiredBuilds);
                Interlocked.Exchange(ref _previewCaptured, 0);
                _resourceAdapter.BeginDocument(document);
                _effectiveApplicationDark = ResolveApplicationDark(document.Appearance.Theme);
                _themeInitialized = true;
                ApplyColorScheme(_effectiveApplicationDark);
                RuntimeError.Visibility = Visibility.Collapsed;
                SetControllerVisible(true);
                WriteInitializationDiagnostics("navigate-to-string");
                _logger.Write(
                    AppLogLevel.Information,
                    "webview-render",
                    $"Render={RenderCorrelation(document)}; Generation={document.CatalogGeneration}; " +
                    $"Frames={document.Frames.Length}");
                _renderStartedTimestamp = Stopwatch.GetTimestamp();
                _readyFrameIds.Clear();
                _firstFrameReadyMilliseconds = 0;
                _renderCompletionToken = document.RenderToken;
                _renderCompletion = new TaskCompletionSource<ReaderRenderCompletion>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref _navigationSubmitCount);
                WriteRenderDiagnostics("navigate-submit", document, version);
                core.Navigate(
                    DictionaryNetworkPolicy.ShellUri(document.RenderToken));
                DebugLookupProbe.Hit(
                    _logger,
                    "NavigationSubmitted",
                    renderToken: document.RenderToken,
                    detail: $"Version={version}");
                ApplyZoom();
            },
            stage =>
            {
                WriteRenderPipelineDiagnostics(stage, document, version);
                DebugLookupProbe.Hit(
                    _logger,
                    stage.ToString(),
                    renderToken: document.RenderToken,
                    detail: $"Version={version}");
            },
            cancellationToken).ConfigureAwait(true);
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        if (_eventsAttached)
        {
            return;
        }

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.FrameNavigationStarting += Core_FrameNavigationStarting;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebResourceRequested += Core_WebResourceRequested;
        core.PermissionRequested += Core_PermissionRequested;
        core.DownloadStarting += Core_DownloadStarting;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.ProcessFailed += Core_ProcessFailed;
        core.FrameCreated += Core_FrameCreated;
        core.ContextMenuRequested += Core_ContextMenuRequested;
        _selectionLookupMenuItem = core.Environment.CreateContextMenuItem(
            _localizer.Get("ReaderHost_ContextMenu_LookupSelection"),
            null,
            CoreWebView2ContextMenuItemKind.Command);
        _selectionLookupMenuItem.CustomItemSelected += SelectionLookupMenuItem_CustomItemSelected;
        _eventsAttached = true;
    }

    private void DetachCoreEvents()
    {
        CoreWebView2? core = Core;
        if (core is null)
        {
            _eventsAttached = false;
            return;
        }

        core.NavigationStarting -= Core_NavigationStarting;
        core.NavigationCompleted -= Core_NavigationCompleted;
        core.FrameNavigationStarting -= Core_FrameNavigationStarting;
        core.NewWindowRequested -= Core_NewWindowRequested;
        core.WebResourceRequested -= Core_WebResourceRequested;
        core.PermissionRequested -= Core_PermissionRequested;
        core.DownloadStarting -= Core_DownloadStarting;
        core.WebMessageReceived -= Core_WebMessageReceived;
        core.ProcessFailed -= Core_ProcessFailed;
        core.FrameCreated -= Core_FrameCreated;
        core.ContextMenuRequested -= Core_ContextMenuRequested;
        if (_selectionLookupMenuItem is not null)
        {
            _selectionLookupMenuItem.CustomItemSelected -= SelectionLookupMenuItem_CustomItemSelected;
            _selectionLookupMenuItem = null;
        }
        foreach (CoreWebView2Frame frame in _frames.ToArray())
        {
            DetachFrame(frame);
        }

        _frames.Clear();
        _frameCorrelations.Clear();
        _eventsAttached = false;
    }

    private void CloseController()
    {
        DetachCoreEvents();
        _lastControllerBounds = null;
        _lastRasterizationScale = 0;
        BrowserSurface.Close();
    }

    private void UpdateControllerLayout(
        bool notifyParentPositionChanged = false,
        bool forceVisibility = false)
    {
        if (!HasUsableLayout())
        {
            UpdateControllerVisibility(forceVisibility);
            return;
        }

        try
        {
            XamlRoot xamlRoot = XamlRoot ??
                throw new InvalidOperationException("Reader surface has no XamlRoot.");
            UIElement windowContent = xamlRoot.Content as UIElement ??
                throw new InvalidOperationException("Window XAML content root is unavailable.");
            Windows.Foundation.Point origin = ReaderSurface
                .TransformToVisual(windowContent)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = xamlRoot.RasterizationScale;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                throw new InvalidOperationException("Window rasterization scale is invalid.");
            }

            Windows.Foundation.Rect surfaceBounds = new(
                origin.X,
                origin.Y,
                ReaderSurface.ActualWidth,
                ReaderSurface.ActualHeight);
            PhysicalPixelBounds pixels = DpiBoundsConverter.AlignInside(
                surfaceBounds.X,
                surfaceBounds.Y,
                surfaceBounds.Width,
                surfaceBounds.Height,
                scale);
            Windows.Foundation.Rect controllerBounds = new(
                pixels.X,
                pixels.Y,
                pixels.Width,
                pixels.Height);
            if (controllerBounds.Width <= 0 || controllerBounds.Height <= 0)
            {
                UpdateControllerVisibility(forceVisibility);
                return;
            }

            bool scaleChanged = !NearlyEqual(_lastRasterizationScale, scale);
            bool boundsChanged = _lastControllerBounds is not { } previousBounds ||
                !RectEquals(previousBounds, controllerBounds);
            _lastRasterizationScale = scale;
            _lastControllerBounds = controllerBounds;

            UpdateControllerVisibility(forceVisibility);
            if (scaleChanged || boundsChanged)
            {
                WriteLayoutDiagnostics(surfaceBounds, controllerBounds, scale);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-layout",
                $"Controller bounds update failed: {exception.GetType().Name}");
        }
    }

    private void SetControllerVisible(bool visible)
    {
        _contentVisibleRequested = visible;
        UpdateControllerVisibility();
    }

    private void UpdateControllerVisibility(bool force = false)
    {
        bool visible = _contentVisibleRequested &&
            _visibilitySuppressionCount == 0 &&
            HasUsableLayout() &&
            RuntimeError.Visibility != Visibility.Visible;
        BrowserSurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsParentWindowMinimized() =>
        _parentAppWindow?.Presenter is OverlappedPresenter presenter &&
        presenter.State == OverlappedPresenterState.Minimized;

    private void ReleaseVisibilitySuppression()
    {
        if (_visibilitySuppressionCount > 0)
        {
            _visibilitySuppressionCount--;
        }

        UpdateControllerLayout();
    }

    private static bool RectEquals(
        Windows.Foundation.Rect left,
        Windows.Foundation.Rect right) =>
        NearlyEqual(left.X, right.X) &&
        NearlyEqual(left.Y, right.Y) &&
        NearlyEqual(left.Width, right.Width) &&
        NearlyEqual(left.Height, right.Height);

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.0001;

    private void WriteLayoutDiagnostics(
        Windows.Foundation.Rect surfaceBounds,
        Windows.Foundation.Rect controllerBounds,
        double scale)
    {
        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            return;
        }

        nint hwnd = _mainWindowAccessor.Window is { } owner
            ? WindowNative.GetWindowHandle(owner)
            : 0;
        NativeRect client = default;
        bool hasClientRect = hwnd != 0 && GetClientRect(hwnd, out client);
        double clientWidthDip = hasClientRect ? client.Width / scale : 0;
        double clientHeightDip = hasClientRect ? client.Height / scale : 0;
        _logger.Write(
            AppLogLevel.Information,
            "webview-layout",
            $"SurfaceDip={FormatRect(surfaceBounds)}; " +
            $"ControllerPx={FormatRect(controllerBounds)}; " +
            $"Host=XamlWebView2; RasterizationScale={scale:F4}; " +
            $"ClientPx={client.Width}x{client.Height}; " +
            $"ClientDip={clientWidthDip:F2}x{clientHeightDip:F2}; " +
            $"Loaded={IsLoaded}; Visible={BrowserSurface.Visibility == Visibility.Visible}; " +
            $"Minimized={IsParentWindowMinimized()}; Suppressions={_visibilitySuppressionCount}");
    }

    private static string FormatRect(Windows.Foundation.Rect rect) =>
        $"({rect.X:F3},{rect.Y:F3},{rect.Width:F3},{rect.Height:F3})";

    private void Core_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            TryResolveShellRequest(args.Uri))
        {
            _navigationState.NavigationStarted(args.NavigationId);
            WriteRenderTimeline("NavigationStarting", 0, 0);
            return;
        }

        args.Cancel = true;
        StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_NavigationBlocked"));
    }

    private async void Core_NavigationCompleted(
        CoreWebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        NavigationCompletionDisposition disposition =
            _navigationState.NavigationCompleted(args.NavigationId, args.IsSuccess);
        if (disposition == NavigationCompletionDisposition.Ignored)
        {
            return;
        }

        ApplyZoom();
        DebugLookupProbe.Hit(
            _logger,
            args.IsSuccess ? "NavigationCompleted" : "NavigationFailed",
            renderToken: _document?.RenderToken,
            detail: $"Navigation={args.NavigationId}");
        WriteRenderTimeline(
            args.IsSuccess ? "NavigationCompleted" : "NavigationFailed",
            0,
            0);
        if (disposition == NavigationCompletionDisposition.Failed)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-navigation",
                $"Render={RenderCorrelation(_document)}; Navigation={args.NavigationId}; " +
                $"Success=false; WebErrorStatus={args.WebErrorStatus}");
            ShowContentError();
            return;
        }

        RuntimeError.Visibility = Visibility.Collapsed;
        SetControllerVisible(true);
        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            return;
        }

        try
        {
            string frameCount = await sender.ExecuteScriptAsync(
                "document.querySelectorAll('iframe.entry').length");
            _logger.Write(
                AppLogLevel.Information,
                "webview-navigation",
                $"Render={RenderCorrelation(_document)}; Navigation={args.NavigationId}; " +
                $"完成={args.IsSuccess}；状态={args.WebErrorStatus}；正文框架数={frameCount}");
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-navigation",
                "无法读取导航诊断。",
                exception);
        }
    }

    private async void Core_NewWindowRequested(
        CoreWebView2 sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        Windows.Foundation.Deferral deferral = args.GetDeferral();
        try
        {
            if (!args.IsUserInitiated ||
                !Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
                uri.Scheme is not ("http" or "https") ||
                _catalog.GetSettingsSnapshot().ExternalLinkPolicy !=
                    ExternalLinkPolicy.SystemBrowser)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_NewWindowBlocked"));
                return;
            }

            await OpenExternalLinkAsync(uri, args.IsUserInitiated).ConfigureAwait(true);
            return;
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-navigation",
                "打开词典新窗口链接失败。",
                exception);
        }
        finally
        {
            deferral.Complete();
        }

        StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_NewWindowBlocked"));
    }

    private void Core_FrameCreated(
        CoreWebView2 sender,
        CoreWebView2FrameCreatedEventArgs args)
    {
        CoreWebView2Frame frame = args.Frame;
        _frames.Add(frame);
        string correlation = $"frame-{Interlocked.Increment(ref _frameSequence):x8}";
        _frameCorrelations[frame] = correlation;
        frame.NavigationStarting += Frame_NavigationStarting;
        frame.DOMContentLoaded += Frame_DOMContentLoaded;
        frame.Destroyed += Frame_Destroyed;
        if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            _logger.Write(
                AppLogLevel.Information,
                "webview-frame",
                $"Render={RenderCorrelation(_document)}; Frame={correlation}; Created=true");
        }
    }

    private async void Frame_DOMContentLoaded(
        CoreWebView2Frame sender,
        CoreWebView2DOMContentLoadedEventArgs args)
    {
        try
        {
            string ready = await sender.ExecuteScriptAsync(
                "Boolean(document.body?.textContent?.trim()) && " +
                "window.__vdictRuntimeReady === true");
            if (ready.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                _document is { } document)
            {
                AutomationProperties.SetHelpText(ReaderSurface, $"Rendered:{document.Word}");
            }

            if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
            {
                await CaptureFrameDiagnosticsAsync(sender, "dom-content-loaded")
                    .ConfigureAwait(true);
                await Task.Delay(750).ConfigureAwait(true);
                await CaptureFrameDiagnosticsAsync(sender, "settled")
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-frame",
                "无法验证词典正文框架就绪状态。",
                exception);
        }
    }

    private void Frame_Destroyed(CoreWebView2Frame sender, object args)
    {
        DetachFrame(sender);
        _frames.Remove(sender);
        _frameCorrelations.Remove(sender);
    }

    private void DetachFrame(CoreWebView2Frame frame)
    {
        frame.NavigationStarting -= Frame_NavigationStarting;
        frame.DOMContentLoaded -= Frame_DOMContentLoaded;
        frame.Destroyed -= Frame_Destroyed;
    }

    private void Core_FrameNavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            TryResolveFrameRequest(args.Uri, out _))
        {
            return;
        }

        args.Cancel = true;
        if (args.IsUserInitiated &&
            Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme is "http" or "https" &&
            _catalog.GetSettingsSnapshot().ExternalLinkPolicy ==
                ExternalLinkPolicy.SystemBrowser)
        {
            _ = OpenExternalLinkAsync(uri, args.IsUserInitiated);
            return;
        }

        StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_FrameNavigationBlocked"));
    }

    private void Frame_NavigationStarting(
        CoreWebView2Frame sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
            TryResolveFrameRequest(args.Uri, out _))
        {
            return;
        }

        // CoreWebView2.FrameNavigationStarting runs before the frame-specific
        // handler and shares the same arguments. Reassert cancellation here so
        // no later frame handler can restore an external dictionary navigation.
        args.Cancel = true;
        ExternalLinkPolicy policy = _catalog.GetSettingsSnapshot().ExternalLinkPolicy;
        if (policy != ExternalLinkPolicy.SystemBrowser)
        {
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_FrameNavigationBlocked"));
        }

        if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            _logger.Write(
                AppLogLevel.Information,
                "webview-navigation",
                $"Render={RenderCorrelation(_document)}; " +
                $"Frame={_frameCorrelations.GetValueOrDefault(sender, "unknown")}; " +
                $"Uri={args.Uri}; UserInitiated={args.IsUserInitiated}; " +
                $"Policy={policy}; Cancelled=true");
        }
    }

    private async void Core_WebResourceRequested(
        CoreWebView2 sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        Windows.Foundation.Deferral deferral = args.GetDeferral();
        try
        {
            string uriValue = args.Request.Uri;
            if (DictionaryNetworkPolicy.IsWelcomeLogoUri(uriValue))
            {
                byte[]? logo = WelcomeLogo.Value;
                if (logo is null)
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        new InMemoryRandomAccessStream(),
                        404,
                        "Not Found",
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                    return;
                }

                IRandomAccessStream logoContent = await CreateStreamAsync(logo);
                args.Response = sender.Environment.CreateWebResourceResponse(
                    logoContent,
                    200,
                    "OK",
                    "Content-Type: image/png\r\n" +
                    "Cache-Control: public, max-age=86400\r\n" +
                    "X-Content-Type-Options: nosniff");
                return;
            }

            if (TryResolveShellRequest(uriValue) && _document is { } shellDocument)
            {
                Stopwatch buildTimer = Stopwatch.StartNew();
                byte[] body = Encoding.UTF8.GetBytes(
                    ReaderHtmlRenderer.BuildShell(
                        shellDocument,
                        _effectiveApplicationDark));
                buildTimer.Stop();
                Stopwatch streamTimer = Stopwatch.StartNew();
                IRandomAccessStream content = await CreateStreamAsync(body);
                streamTimer.Stop();
                args.Response = sender.Environment.CreateWebResourceResponse(
                    content,
                    200,
                    "OK",
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "X-Content-Type-Options: nosniff");
                WriteRenderTimeline(
                    "ShellResponse",
                    buildTimer.Elapsed.TotalMilliseconds,
                    body.Length);
                WriteRenderTimeline(
                    "ShellStream",
                    streamTimer.Elapsed.TotalMilliseconds,
                    body.Length);
                return;
            }

            if (TryResolveFrameRequest(uriValue, out ReaderFrame? frame) &&
                frame is { } resolvedFrame &&
                _document is { } document)
            {
                Stopwatch buildTimer = Stopwatch.StartNew();
                ReaderFrameBuildResult frameBuild = _backgroundFrameBuildEnabled
                    ? await GetFrameBuildAsync(
                        resolvedFrame,
                        document).ConfigureAwait(true)
                    : ReaderHtmlRenderer.BuildFrame(
                        resolvedFrame,
                        document,
                        DarkModeAssets.Value,
                        effectiveApplicationDark: _effectiveApplicationDark);
                if (_document?.RenderToken != document.RenderToken)
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        new InMemoryRandomAccessStream(),
                        410,
                        "Stale Render",
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                    return;
                }
                byte[] body = Encoding.UTF8.GetBytes(frameBuild.Html);
                buildTimer.Stop();
                if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
                {
                    DictionaryHtmlNormalizationDiagnostics diagnostics =
                        frameBuild.Diagnostics;
                    _logger.Write(
                        diagnostics.Kind is DictionaryHtmlKind.Ambiguous or
                            DictionaryHtmlKind.Composite
                            ? AppLogLevel.Warning
                            : AppLogLevel.Information,
                        "html-normalization",
                        $"Render={RenderCorrelation(document)}; " +
                        $"Frame={resolvedFrame.FrameId}; " +
                        $"Dictionary={resolvedFrame.DictionaryId.Value}; " +
                        $"Kind={diagnostics.Kind}; " +
                        $"SourceLength={diagnostics.SourceLength}; " +
                        $"HtmlCount={diagnostics.HtmlElementCount}; " +
                        $"HeadCount={diagnostics.HeadElementCount}; " +
                        $"BodyCount={diagnostics.BodyElementCount}; " +
                        $"RemovedWrapperLength={diagnostics.RemovedWrapperLength}; " +
                        $"RemovedSecurityLength={diagnostics.RemovedSecurityLength}; " +
                        $"RelocatedStyleLength={diagnostics.RelocatedStyleLength}; " +
                        $"DroppedPayloadLength={diagnostics.DroppedPayloadLength}; " +
                        $"Warnings={diagnostics.Warnings}");
                }

                Stopwatch streamTimer = Stopwatch.StartNew();
                IRandomAccessStream content = await CreateStreamAsync(body);
                streamTimer.Stop();
                args.Response = sender.Environment.CreateWebResourceResponse(
                    content,
                    200,
                    "OK",
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "X-Content-Type-Options: nosniff");
                WriteRenderTimeline(
                    $"FrameResponse:{resolvedFrame.FrameId}",
                    buildTimer.Elapsed.TotalMilliseconds,
                    body.Length);
                WriteRenderTimeline(
                    $"FrameStream:{resolvedFrame.FrameId}",
                    streamTimer.Elapsed.TotalMilliseconds,
                    body.Length);
                return;
            }

            if (DictionaryNetworkPolicy.TryParseResourceUri(
                    uriValue,
                    out _,
                    out _,
                    out _,
                    out _) ||
                DictionaryNetworkPolicy.TryParseLegacyResourceUri(
                    uriValue,
                    out _,
                    out _))
            {
                string resourceCorrelation = Guid.NewGuid().ToString("N")[..8];
                bool diagnosticsEnabled =
                    _catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled;
                await using ResourceMemoryProbe? memoryProbe =
                    diagnosticsEnabled ? new ResourceMemoryProbe() : null;
                Stopwatch resourceTimer = Stopwatch.StartNew();
                string? origin = Header(args, "Origin");
                string? referer = Header(args, "Referer");
                string? range = Header(args, "Range");
                ReaderResourceResponse response = await _resourceAdapter.ResolveAsync(new(
                    uriValue,
                    origin,
                    referer,
                    range)
                {
                    Method = args.Request.Method
                }).ConfigureAwait(true);
                double resolveMilliseconds = resourceTimer.Elapsed.TotalMilliseconds;
                IRandomAccessStream content = await CreateStreamAsync(response.Body);
                resourceTimer.Stop();
                ResourceMemoryObservation memory = memoryProbe is null
                    ? ResourceMemoryObservation.Empty
                    : await memoryProbe.StopAsync().ConfigureAwait(true);
                if (diagnosticsEnabled)
                {
                    _ = DictionaryNetworkPolicy.TryParseFrameUri(
                        referer,
                        out _,
                        out string requestRenderToken,
                        out string requestFrameId);
                    string contentRange = response.Headers.TryGetValue(
                        "Content-Range", out string? resolvedRange)
                        ? resolvedRange
                        : string.Empty;
                    string contentLength = response.Headers.TryGetValue(
                        "Content-Length", out string? resolvedLength)
                        ? resolvedLength
                        : response.Body.Length.ToString(CultureInfo.InvariantCulture);
                    _logger.Write(
                        response.StatusCode >= 400 || !string.IsNullOrWhiteSpace(response.Diagnostic)
                            ? AppLogLevel.Warning
                            : AppLogLevel.Information,
                        "compatibility",
                        $"Render={RenderCorrelation(_document)}; Resource={resourceCorrelation}; " +
                        $"Method={args.Request.Method}; Uri={uriValue}; Origin={origin ?? string.Empty}; " +
                        $"Referer={referer ?? string.Empty}; Dictionary={response.DictionaryId ?? string.Empty}; " +
                        $"Generation={response.Generation}; Frame={requestFrameId}; " +
                        $"RequestRender={requestRenderToken}; Path={response.NormalizedPath ?? string.Empty}; " +
                        $"LookupKey={response.LookupKey ?? string.Empty}; Source={response.Source ?? string.Empty}; " +
                        $"CacheHit={response.Source == "memory"}; Range={range ?? string.Empty}; " +
                        $"MddInitMs={response.MddInitializationMilliseconds?.ToString("F3", CultureInfo.InvariantCulture) ?? string.Empty}; " +
                        $"PersistentIndexHit={response.PersistentIndexHit?.ToString() ?? string.Empty}; " +
                        $"Status={response.StatusCode}; ContentType={response.MimeType}; " +
                        $"ContentLength={contentLength}; FullLength={response.FullLength}; " +
                        $"ReturnedBytes={response.Body.Length}; ContentRange={contentRange}; " +
                        $"SourceBytesRead={response.ReadDiagnostics.SourceBytesRead}; " +
                        $"RecordBlockBytesDecoded={response.ReadDiagnostics.RecordBlockBytesDecoded}; " +
                        $"ResourceBytesMaterialized={response.ReadDiagnostics.ResourceBytesMaterialized}; " +
                        $"ResourceArrayCopies={response.ReadDiagnostics.ResourceArrayCopyCount}; " +
                        $"DecodedResourceCacheHit={response.ReadDiagnostics.DecodedResourceCacheHit}; " +
                        $"RecordBlockCacheHit={response.ReadDiagnostics.RecordBlockCacheHit?.ToString() ?? string.Empty}; " +
                        $"ResponseStreamCopiedBytes={response.Body.Length * 2L}; " +
                        $"ResponseStreamCopyCount={(response.Body.IsEmpty ? 0 : 2)}; " +
                        $"ManagedHeapPeakDelta={memory.ManagedHeapPeakDelta}; " +
                        $"WorkingSetPeakDelta={memory.WorkingSetPeakDelta}; " +
                        $"ResolveElapsedMs={resolveMilliseconds:F3}; " +
                        $"ElapsedMs={resourceTimer.Elapsed.TotalMilliseconds:F3}; " +
                        $"RenderElapsedMs={RenderElapsedMilliseconds():F3}; " +
                        (response.Diagnostic ?? "Dictionary resource resolved."));
                }

                args.Response = sender.Environment.CreateWebResourceResponse(
                    content,
                    response.StatusCode,
                    response.ReasonPhrase,
                    BuildResponseHeaders(response));

                return;
            }

            if (DictionaryNetworkPolicy.IsInternalDocumentUri(uriValue))
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    new InMemoryRandomAccessStream(),
                    410,
                    "Stale or Invalid",
                    "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                return;
            }

            if (ReaderWebResourceAdapter.ShouldBlockRemote(
                    uriValue,
                    Header(args, "Origin"),
                    Header(args, "Referer"),
                    _document))
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    new InMemoryRandomAccessStream(),
                    403,
                    "Blocked",
                    "Cache-Control: no-store");
            }
        }
        catch (OperationCanceledException)
        {
            args.Response = sender.Environment.CreateWebResourceResponse(
                new InMemoryRandomAccessStream(),
                410,
                "Stale Render",
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "webview-download", "下载未开始。", exception);
            args.Response = sender.Environment.CreateWebResourceResponse(
                new InMemoryRandomAccessStream(),
                500,
                "Resource Error",
                "Cache-Control: no-store");
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_ResourceFailed"));
        }
        finally
        {
            deferral.Complete();
        }
    }

    private bool TryResolveFrameRequest(string? uriValue, out ReaderFrame? frame)
    {
        frame = null;
        ReaderDocument? document = _document;
        if (document is null ||
            !DictionaryNetworkPolicy.TryParseFrameUri(
                uriValue,
                out string host,
                out string renderToken,
                out string frameId) ||
            !renderToken.Equals(document.RenderToken, StringComparison.Ordinal))
        {
            return false;
        }

        frame = document.Frames.FirstOrDefault(
            candidate =>
                candidate.FrameId.Equals(frameId, StringComparison.Ordinal) &&
                DictionaryNetworkPolicy.FrameHostForDictionary(candidate.DictionaryId.Value)
                    .Equals(host, StringComparison.OrdinalIgnoreCase));
        return frame is not null;
    }

    private Task<ReaderFrameBuildResult> GetFrameBuildAsync(
        ReaderFrame frame,
        ReaderDocument document)
    {
        string key = document.RenderToken + ":" + frame.FrameId;
        lock (_frameBuildSync)
        {
            if (_frameBuildTasks.TryGetValue(
                    key,
                    out Task<ReaderFrameBuildResult>? existing))
            {
                return existing;
            }

            CancellationToken cancellationToken = _frameBuildCancellation.Token;
            Task<ReaderFrameBuildResult> build = Task.Factory.StartNew(
                () => ReaderHtmlRenderer.BuildFrame(
                    frame,
                    document,
                    DarkModeAssets.Value,
                    cancellationToken,
                    _effectiveApplicationDark),
                cancellationToken,
                TaskCreationOptions.DenyChildAttach,
                _frameBuildScheduler.ExclusiveScheduler);
            _frameBuildTasks[key] = build;
            return build;
        }
    }

    private static async Task DisposeFrameBuildSessionAsync(
        CancellationTokenSource cancellation,
        Task<ReaderFrameBuildResult>[] builds)
    {
        try
        {
            await Task.WhenAll(builds).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private bool TryResolveShellRequest(string? uriValue)
    {
        ReaderDocument? document = _document ?? _pendingDocument;
        return document is not null &&
               DictionaryNetworkPolicy.TryParseShellUri(uriValue, out string renderToken) &&
               renderToken.Equals(document.RenderToken, StringComparison.Ordinal);
    }

    private static void Core_PermissionRequested(
        CoreWebView2 sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        args.Handled = true;
    }

    private async Task CaptureFrameDiagnosticsAsync(
        CoreWebView2Frame frame,
        string stage)
    {
        string diagnostics = await frame.ExecuteScriptAsync(
            ReaderBrowserDiagnostics.FrameProbeScript);
        _logger.Write(
            AppLogLevel.Information,
            "webview-frame-diagnostics",
            $"Render={RenderCorrelation(_document)}; " +
            $"Frame={(_frameCorrelations.TryGetValue(frame, out string? value) ? value : "unknown")}; " +
            $"Stage={stage}; Metrics={diagnostics}");
    }

    private void WriteInitializationDiagnostics(string stage)
    {
        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            return;
        }

        Task? initialization;
        lock (_initializationSync)
        {
            initialization = _initializationTask;
        }

        _logger.Write(
            AppLogLevel.Information,
            "webview-initialization-diagnostics",
            $"Stage={stage}; Lifecycle={_lifecycleState}; " +
            $"InitializationStatus={initialization?.Status.ToString() ?? "none"}; " +
            $"CorePresent={Core is not null}; " +
            $"EventsAttached={_eventsAttached}; HasUsableLayout={HasUsableLayout()}");
    }

    private async void Core_DownloadStarting(
        CoreWebView2 sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Handled = true;
        args.Cancel = true;
        Windows.Foundation.Deferral deferral = args.GetDeferral();
        bool sideEffectLockTaken = false;
        try
        {
            ReaderDocument? document = _document;
            if (document is null)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadSessionInvalid"));
                return;
            }

            if (Uri.TryCreate(args.DownloadOperation.Uri, UriKind.Absolute, out Uri? downloadUri) &&
                downloadUri.Scheme is "http" or "https" &&
                !DictionaryNetworkPolicy.IsInternalDocumentUri(downloadUri.AbsoluteUri))
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadNetworkBlocked"));
                return;
            }

            sideEffectLockTaken = await _sideEffectGate.WaitAsync(0).ConfigureAwait(true);
            if (!sideEffectLockTaken)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadPendingAction"));
                return;
            }

            string suggested = DownloadFileNamePolicy.Sanitize(
                Path.GetFileName(args.ResultFilePath));
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = _localizer.Get("ReaderHost_Download_Title"),
                Content = _localizer.Format(
                    "ReaderHost_Download_Message",
                    args.DownloadOperation.Uri,
                    suggested),
                PrimaryButtonText = _localizer.Get("ReaderHost_Download_ChooseLocation"),
                CloseButtonText = _localizer.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadCanceled"));
                return;
            }

            _activationGate.Record(
                document.RenderToken,
                TrustedWebAction.Download,
                TrustedWebActivationSource.HostConfirmation);
            if (_document?.RenderToken != document.RenderToken ||
                !_activationGate.TryConsume(
                    document.RenderToken,
                    TrustedWebAction.Download))
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadSessionChanged"));
                return;
            }

            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggested)
            };
            string extension = Path.GetExtension(suggested);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            picker.FileTypeChoices.Add(_localizer.Get("ReaderHost_Download_FileType"), [extension]);
            Microsoft.UI.Xaml.Window owner = _mainWindowAccessor.Window ??
                throw new InvalidOperationException("主窗口尚未创建。");
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(owner));
            Windows.Storage.StorageFile? destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadCanceled"));
                return;
            }

            if (_document?.RenderToken != document.RenderToken)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadSessionChanged"));
                return;
            }

            args.ResultFilePath = destination.Path;
            args.Cancel = false;
            StatusChanged?.Invoke(
                this,
                _localizer.Format("ReaderHost_Status_DownloadingTo", destination.Path));
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Warning, "webview-message", "正文消息处理失败。", exception);
            args.Cancel = true;
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_DownloadFailed"));
        }
        finally
        {
            if (sideEffectLockTaken)
            {
                _sideEffectGate.Release();
            }

            deferral.Complete();
        }
    }

    private async void Core_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        ReaderDocument? document = _document;
        if (document is null)
        {
            return;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(args.WebMessageAsJson);
            if (!DictionaryWebMessageValidator.TryValidateEnvelope(
                    json.RootElement,
                    document.RenderToken,
                    document.Frames,
                    out string kind))
            {
                return;
            }
            string envelopeFrameId = WebMessageString(json.RootElement, "frameId", 256);
            string envelopeDictionaryId = WebMessageString(json.RootElement, "dictionaryId", 128);

            if (kind == "userActivation" &&
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "source",
                    16,
                    out string activationSource) &&
                activationSource is "mouse" or "touch" or "pen" or "keyboard")
            {
                if (activationSource != "keyboard")
                {
                    BrowserSurface.Focus(FocusState.Pointer);
                }

                if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
                {
                    _logger.Write(
                        AppLogLevel.Information,
                        "webview-activation",
                        $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                        $"Source={activationSource}; Trusted=false; AuthorizationIssued=false");
                }

                return;
            }

            if (kind == "inputEvent" &&
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "eventName",
                    20,
                    out string inputEvent) &&
                inputEvent is "pointerdown" or "pointerup" or "click" or "keydown" or "wheel" or "contextmenu")
            {
                double timestamp = json.RootElement.TryGetProperty(
                        "timestamp",
                        out JsonElement timestampValue) &&
                    timestampValue.TryGetDouble(out double parsedTimestamp)
                    ? Math.Max(0, parsedTimestamp)
                    : 0;
                if (inputEvent is "pointerdown" or "pointerup" or "click" or "keydown")
                {
                    DebugLookupProbe.Record(
                        $"WebInput:{inputEvent}",
                        renderToken: document.RenderToken,
                        detail:
                            $"FrameTimestamp={timestamp:F3}; " +
                            $"Trusted={WebMessageBoolean(json.RootElement, "trusted")}; " +
                            $"Target={WebMessageString(json.RootElement, "targetTag", 32)}" +
                            $"#{WebMessageString(json.RootElement, "targetId", 128)}" +
                            $".{WebMessageString(json.RootElement, "targetClass", 256)}");
                }
                AutomationProperties.SetHelpText(
                    ReaderSurface,
                    $"Rendered:{document.Word}; Input:{inputEvent}");
                if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
                {
                    _logger.Write(
                        AppLogLevel.Information,
                        "webview-input",
                        $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                        $"Dictionary={envelopeDictionaryId}; Event={inputEvent}; " +
                        $"FrameTimestamp={timestamp:F3}; Trusted={WebMessageBoolean(json.RootElement, "trusted")}; " +
                        $"Target={WebMessageString(json.RootElement, "targetTag", 32)}" +
                        $"#{WebMessageString(json.RootElement, "targetId", 128)}" +
                        $".{WebMessageString(json.RootElement, "targetClass", 256)}; " +
                        $"AudioTrigger={WebMessageString(json.RootElement, "triggerTag", 32)}" +
                        $".{WebMessageString(json.RootElement, "triggerClass", 256)}; " +
                        $"TriggerHref={WebMessageString(json.RootElement, "triggerHref", 4096)}");
                }
                return;
            }

            if (kind == "selection" &&
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "text",
                    256,
                    out string text) &&
                !string.IsNullOrWhiteSpace(text))
            {
#if DEBUG
                using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                    "WebSelection",
                    $"QueryLength={text.Trim().Length}; Frame={envelopeFrameId}");
#endif
                SelectionLookupRequested?.Invoke(this, text.Trim());
                return;
            }

            if (kind == "entry" &&
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "word",
                    512,
                    out string entry) &&
                !string.IsNullOrWhiteSpace(entry))
            {
#if DEBUG
                using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                    "WebEntry",
                    $"QueryLength={entry.Trim().Length}; Frame={envelopeFrameId}");
#endif
                SelectionLookupRequested?.Invoke(this, entry.Trim());
                return;
            }

            if (kind == "frameReady" &&
                json.RootElement.TryGetProperty("hasContent", out JsonElement hasContent) &&
                hasContent.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                hasContent.GetBoolean())
            {
                AutomationProperties.SetHelpText(ReaderSurface, $"Rendered:{document.Word}");
                RuntimeError.Visibility = Visibility.Collapsed;
                SetControllerVisible(true);
                WriteRenderTimeline($"FrameReady:{envelopeFrameId}", 0, 0);
                if (_readyFrameIds.Add(envelopeFrameId))
                {
                    if (_readyFrameIds.Count == 1)
                    {
                        _firstFrameReadyMilliseconds = RenderElapsedMilliseconds();
                        WriteRenderTimeline("FirstFrameReady", 0, 0);
                        DebugLookupProbe.Hit(
                            _logger,
                            "FirstFrameReady",
                            renderToken: document.RenderToken,
                            detail:
                                $"Frame={envelopeFrameId}; " +
                                $"ElapsedMs={_firstFrameReadyMilliseconds:F3}");
                    }

                    if (_readyFrameIds.Count >= document.Frames.Length)
                    {
                        WriteRenderTimeline("AllFramesReady", 0, 0);
                        if (_renderCompletionToken.Equals(
                                document.RenderToken,
                                StringComparison.Ordinal))
                        {
                            _renderCompletion?.TrySetResult(new ReaderRenderCompletion(
                                document.RenderToken,
                                document.Frames.Length,
                                _firstFrameReadyMilliseconds,
                                RenderElapsedMilliseconds(),
                                Process.GetCurrentProcess().WorkingSet64,
                                GetWebViewProcessMemoryBytes()));
                        }
                    }
                }
                if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled &&
                    Core is { } core)
                {
                    string layout = await core.ExecuteScriptAsync(
                        "JSON.stringify(Array.from(document.querySelectorAll('iframe.entry')).map(" +
                        "frame=>({height:getComputedStyle(frame).height," +
                        "display:getComputedStyle(frame).display," +
                        "visibility:getComputedStyle(frame).visibility," +
                        "rect:frame.getBoundingClientRect().toJSON()})))");
                    _logger.Write(
                        AppLogLevel.Information,
                        "webview-visibility",
                        $"正文可见性诊断：{layout}");
                    string? capturePath =
                        System.Environment.GetEnvironmentVariable("VDICT_READER_CAPTURE");
                    if (!string.IsNullOrWhiteSpace(capturePath) &&
                        Interlocked.CompareExchange(ref _previewCaptured, 1, 0) == 0)
                    {
                        DispatcherQueue.TryEnqueue(
                            () => _ = CaptureReaderPreviewAsync(capturePath));
                    }
                }
                return;
            }

            if (kind == "imageError" &&
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "src",
                    4096,
                    out string failedImage))
            {
                string resourceType = Uri.TryCreate(failedImage, UriKind.Absolute, out Uri? imageUri)
                    ? Path.GetExtension(imageUri.AbsolutePath)
                    : Path.GetExtension(failedImage);
                _logger.Write(
                    AppLogLevel.Warning,
                    "compatibility",
                    $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                    $"Dictionary={envelopeDictionaryId}; ImageDecodeFailed=true; " +
                    $"OriginalUrl={WebMessageString(json.RootElement, "originalSrc", 4096)}; " +
                    $"RewrittenUrl={failedImage}; Type={resourceType[..Math.Min(resourceType.Length, 16)]}; " +
                    $"Complete={WebMessageBoolean(json.RootElement, "complete")}; " +
                    $"NaturalWidth={WebMessageNumber(json.RootElement, "naturalWidth")}; " +
                    $"NaturalHeight={WebMessageNumber(json.RootElement, "naturalHeight")}; " +
                    $"DurationMs={WebMessageNumber(json.RootElement, "duration"):F3}; " +
                    $"TransferSize={WebMessageNumber(json.RootElement, "transferSize")}; " +
                    $"EncodedBodySize={WebMessageNumber(json.RootElement, "encodedBodySize")}; " +
                    $"DecodedBodySize={WebMessageNumber(json.RootElement, "decodedBodySize")}");
                return;
            }

            if (kind == "frameLoad")
            {
                WriteRenderTimeline($"FrameLoad:{envelopeFrameId}", 0, 0);
                return;
            }

            if (kind == "browserDiagnostic")
            {
                string category = WebMessageString(json.RootElement, "category", 32);
                string message = WebMessageString(json.RootElement, "message", 1024);
                if (category == "debug-frame-timing")
                {
                    DebugLookupProbe.Record(
                        $"FrameTiming:{envelopeFrameId}",
                        renderToken: document.RenderToken,
                        detail: message);
                }

                if (_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
                {
                    _logger.Write(
                        category is "error" or "unhandledrejection" or "csp"
                            ? AppLogLevel.Warning
                            : AppLogLevel.Information,
                        "webview-browser",
                        $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                        $"Dictionary={envelopeDictionaryId}; Category={category}; " +
                        $"Message={message}; " +
                        $"Source={WebMessageString(json.RootElement, "source", 4096)}; " +
                        $"Directive={WebMessageString(json.RootElement, "directive", 128)}");
                }

                return;
            }

            if (kind == "zoomDelta" &&
                json.RootElement.TryGetProperty("delta", out JsonElement deltaValue) &&
                deltaValue.TryGetDouble(out double delta))
            {
                int steps = _zoomAccumulator.AddWheelDelta(delta);
                if (steps != 0)
                {
                    ZoomStepsRequested?.Invoke(steps);
                }

                return;
            }

            if (kind == "zoomStep" &&
                json.RootElement.TryGetProperty("step", out JsonElement stepValue) &&
                stepValue.TryGetInt32(out int step) &&
                step is -1 or 1)
            {
                _zoomAccumulator.Reset();
                ZoomStepsRequested?.Invoke(step);
                return;
            }

            if (kind == "zoomReset")
            {
                _zoomAccumulator.Reset();
                ZoomResetRequested?.Invoke();
                return;
            }

            if (kind == "mediaError")
            {
                int code = json.RootElement.TryGetProperty("code", out JsonElement codeValue) &&
                           codeValue.TryGetInt32(out int parsedCode)
                    ? Math.Clamp(parsedCode, 0, 9)
                    : 0;
                int readyState = json.RootElement.TryGetProperty("readyState", out JsonElement readyValue) &&
                                 readyValue.TryGetInt32(out int parsedReady)
                    ? Math.Clamp(parsedReady, 0, 9)
                    : 0;
                int networkState = json.RootElement.TryGetProperty("networkState", out JsonElement networkValue) &&
                                   networkValue.TryGetInt32(out int parsedNetwork)
                    ? Math.Clamp(parsedNetwork, 0, 9)
                    : 0;
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "extension",
                    16,
                    out string extension);
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "errorName",
                    64,
                    out string errorName);
                DictionaryWebMessageValidator.TryGetBoundedString(
                    json.RootElement,
                    "errorMessage",
                    512,
                    out string errorMessage);
                _logger.Write(
                    AppLogLevel.Warning,
                    "webview-media",
                    $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                    $"Dictionary={envelopeDictionaryId}; MediaError={code}; " +
                    $"ReadyState={readyState}; NetworkState={networkState}; Type={extension}; " +
                    $"ErrorName={errorName}; ErrorMessage={errorMessage}");
                return;
            }

            if (kind == "mediaWarmup" &&
                _catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
            {
                _logger.Write(
                    WebMessageString(json.RootElement, "eventName", 32) == "failed"
                        ? AppLogLevel.Warning
                        : AppLogLevel.Information,
                    "webview-media",
                    $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                    $"Dictionary={envelopeDictionaryId}; WarmupEvent=" +
                    $"{WebMessageString(json.RootElement, "eventName", 32)}; " +
                    $"SourceCount={WebMessageNumber(json.RootElement, "sourceCount")}; " +
                    $"PendingCount={WebMessageNumber(json.RootElement, "pendingCount")}; " +
                    $"ElapsedMs={WebMessageNumber(json.RootElement, "elapsedMs"):F3}; " +
                    $"CurrentSrc={WebMessageString(json.RootElement, "src", 4096)}; " +
                    $"ErrorName={WebMessageString(json.RootElement, "errorName", 64)}");
                return;
            }

            if (kind == "mediaEvent" &&
                _catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
            {
                string mediaEvent = WebMessageString(json.RootElement, "eventName", 32);
                _logger.Write(
                    mediaEvent is "playPromise" &&
                    !WebMessageString(json.RootElement, "promiseOutcome", 32)
                        .Equals("resolved", StringComparison.Ordinal)
                        ? AppLogLevel.Warning
                        : AppLogLevel.Information,
                    "webview-media",
                    $"Render={RenderCorrelation(document)}; Frame={envelopeFrameId}; " +
                    $"Dictionary={envelopeDictionaryId}; Event={mediaEvent}; " +
                    $"ReadyState={WebMessageNumber(json.RootElement, "readyState")}; " +
                    $"NetworkState={WebMessageNumber(json.RootElement, "networkState")}; " +
                    $"CurrentSrc={WebMessageString(json.RootElement, "currentSrc", 4096)}; " +
                    $"Preload={WebMessageString(json.RootElement, "preload", 16)}; " +
                    $"Paused={WebMessageBoolean(json.RootElement, "paused")}; " +
                    $"Ended={WebMessageBoolean(json.RootElement, "ended")}; " +
                    $"CurrentTime={WebMessageNumber(json.RootElement, "currentTime"):F3}; " +
                    $"Duration={WebMessageNumber(json.RootElement, "duration"):F3}; " +
                    $"ErrorCode={WebMessageNumber(json.RootElement, "errorCode")}; " +
                    $"ErrorMessage={WebMessageString(json.RootElement, "errorMessage", 512)}; " +
                    $"UserActivationActive={WebMessageBoolean(json.RootElement, "userActivationActive")}; " +
                    $"Promise={WebMessageString(json.RootElement, "promiseOutcome", 32)}; " +
                    $"PlayLatencyMs={WebMessageNumber(json.RootElement, "playLatencyMs"):F3}");
                return;
            }

            if (kind == "mediaMetadata" &&
                _catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
            {
                double duration = json.RootElement.TryGetProperty("duration", out JsonElement durationValue) &&
                                  durationValue.TryGetDouble(out double parsedDuration)
                    ? parsedDuration
                    : -1;
                _logger.Write(
                    AppLogLevel.Information,
                    "webview-media",
                    $"Render={RenderCorrelation(document)}; MetadataLoaded=true; Duration={duration:F3}");
                return;
            }

        }
        catch (JsonException)
        {
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-message",
                "正文消息处理失败。",
                exception);
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_MessageFailed"));
        }
    }

    private async Task CaptureReaderPreviewAsync(string capturePath)
    {
        try
        {
            await Task.Yield();
            if (Core is not { } core)
            {
                return;
            }

            string fullCapturePath = Path.GetFullPath(capturePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullCapturePath) ??
                throw new InvalidOperationException("正文截图路径无效。"));
            using InMemoryRandomAccessStream capture = new();
            await core.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png,
                capture);
            capture.Seek(0);
            using DataReader reader = new(capture.GetInputStreamAt(0));
            uint length = checked((uint)capture.Size);
            await reader.LoadAsync(length);
            byte[] image = new byte[length];
            reader.ReadBytes(image);
            await File.WriteAllBytesAsync(fullCapturePath, image);
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-visibility",
                "无法捕获正文实际渲染位图。",
                exception);
        }
    }

    private void Core_ProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        _logger.Write(
            AppLogLevel.Error,
            "webview-process",
            $"WebView2 进程失败：{args.ProcessFailedKind}；原因：{args.Reason}；" +
            $"退出码：{args.ExitCode}；描述：{args.ProcessDescription}");
        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.GpuProcessExited)
        {
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_GpuRecovered"));
            return;
        }

        StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_ProcessRecovering"));
        if (_lifecycleState is not (
                ReaderWebViewLifecycleState.Recovering or
                ReaderWebViewLifecycleState.Disposed))
        {
            _lifecycleState = ReaderWebViewLifecycleState.Recovering;
            _ = DispatcherQueue.TryEnqueue(
                () => _ = RecoverFromProcessFailureAsync(args.ProcessFailedKind));
        }
    }

    private async Task RecoverFromProcessFailureAsync(CoreWebView2ProcessFailedKind kind)
    {
        try
        {
            if (_lifecycleState == ReaderWebViewLifecycleState.Disposed)
            {
                return;
            }

            // A browser-process exit invalidates every frame immediately. Calling
            // ExecuteScriptAsync on those stale COM proxies only adds a second
            // failure path while recovery is replacing the controller.
            if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                await StopMediaAsync().ConfigureAwait(true);
            }
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                RecreateWebViewControl();
                _environment = null;
                lock (_initializationSync)
                {
                    _initializationTask = null;
                    _lifecycleState = ReaderWebViewLifecycleState.NotStarted;
                }
            }
            else
            {
                lock (_initializationSync)
                {
                    _initializationTask = null;
                    _lifecycleState = ReaderWebViewLifecycleState.NotStarted;
                }
            }

            ReaderDocument? pending = _pendingDocument ?? _document;
            if (pending is { } document)
            {
                long version = _navigationState.RegisterDocument();
                _pendingDocument = document;
                await RenderDocumentAsync(document, version, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            if (_lifecycleState != ReaderWebViewLifecycleState.Disposed)
            {
                _lifecycleState = ReaderWebViewLifecycleState.Failed;
            }
            _logger.Write(
                AppLogLevel.Error,
                "webview-process",
                "WebView2 进程恢复失败。",
                exception);
            ShowRuntimeError();
        }
    }

    private void RecreateWebViewControl()
    {
        DetachCoreEvents();
        BrowserSurface.Close();
        ReaderSurface.Children.Remove(BrowserSurface);
        BrowserSurface = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ReaderSurface.Children.Insert(0, BrowserSurface);
        _lastControllerBounds = null;
        _lastRasterizationScale = 0;
        UpdateControllerVisibility(force: true);
    }

    private async Task StopMediaAsync()
    {
        CoreWebView2Frame[] frames = _frames.ToArray();
        if (frames.Length == 0)
        {
            return;
        }

        Task[] tasks = new Task[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            CoreWebView2Frame frame = frames[i];
            tasks[i] = StopSingleFrameMediaAsync(frame);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task StopSingleFrameMediaAsync(CoreWebView2Frame frame)
    {
        try
        {
            _ = await frame.ExecuteScriptAsync("window.vdictStopMedia?.(); true");
        }
        catch (Exception exception)
            when (exception is COMException or InvalidOperationException)
        {
            DetachFrame(frame);
            _frames.Remove(frame);
            _frameCorrelations.Remove(frame);
            _logger.Write(
                AppLogLevel.Warning,
                "webview-media",
                $"Render={RenderCorrelation(_document)}; StopMediaFailed={exception.GetType().Name}");
        }
    }

    private async Task OpenExternalLinkAsync(Uri uri, bool webViewUserInitiated)
    {
        bool sideEffectLockTaken = false;
        try
        {
            ReaderDocument? document = _document;
            if (!webViewUserInitiated ||
                document is null ||
                _catalog.GetSettingsSnapshot().ExternalLinkPolicy !=
                    ExternalLinkPolicy.SystemBrowser)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_LinkUntrusted"));
                return;
            }

            sideEffectLockTaken = await _sideEffectGate.WaitAsync(0).ConfigureAwait(true);
            if (!sideEffectLockTaken)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_LinkPendingAction"));
                return;
            }

            _activationGate.Record(
                document.RenderToken,
                TrustedWebAction.ExternalLink,
                TrustedWebActivationSource.WebViewUserInitiated);
            if (!_activationGate.TryConsume(
                    document.RenderToken,
                    TrustedWebAction.ExternalLink))
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_LinkActivationInvalid"));
                return;
            }

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = _localizer.Get("ReaderHost_Link_Title"),
                Content = _localizer.Format("ReaderHost_Link_Message", uri.Host),
                PrimaryButtonText = _localizer.Get("ReaderHost_Link_Open"),
                CloseButtonText = _localizer.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                _document?.RenderToken != document.RenderToken)
            {
                StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_LinkCanceled"));
                return;
            }

            _ = await _externalLinks.OpenAsync(uri).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-navigation",
                "Unable to open a user-initiated external link.",
                exception);
            StatusChanged?.Invoke(this, _localizer.Get("ReaderHost_Status_LinkFailed"));
        }
        finally
        {
            if (sideEffectLockTaken)
            {
                _sideEffectGate.Release();
            }
        }
    }

    private void Core_ContextMenuRequested(
        CoreWebView2 sender,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        bool hasSelection = _contextSelection.BeginMenu(
            args.ContextMenuTarget.HasSelection,
            args.ContextMenuTarget.SelectionText);
        if (!hasSelection || _selectionLookupMenuItem is null)
        {
            return;
        }

        args.MenuItems.Insert(0, _selectionLookupMenuItem);
    }

    private void SelectionLookupMenuItem_CustomItemSelected(object? sender, object args)
    {
        string selection = _contextSelection.Value;
        _contextSelection.Clear();
        if (!string.IsNullOrWhiteSpace(selection))
        {
            SelectionLookupRequested?.Invoke(this, selection);
        }
    }

    private async void InstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        _ = await _externalLinks.OpenAsync(RuntimeHelpUri).ConfigureAwait(true);
    }

    private async void RetryRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            lock (_initializationSync)
            {
                if (_lifecycleState != ReaderWebViewLifecycleState.Ready)
                {
                    _initializationTask = null;
                    _lifecycleState = ReaderWebViewLifecycleState.NotStarted;
                }
            }

            ReaderDocument? pending = _pendingDocument ?? _document;
            if (pending is null)
            {
                await InitializeFromEventAsync().ConfigureAwait(true);
                return;
            }

            long version = _navigationState.RegisterDocument();
            _pendingDocument = pending;
            await RenderDocumentAsync(pending, version, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Error,
                "webview-navigation",
                "Reader retry failed.",
                exception);
            ShowContentError();
        }
    }

    private void ShowRuntimeError()
    {
        if (_lifecycleState != ReaderWebViewLifecycleState.Disposed)
        {
            _lifecycleState = ReaderWebViewLifecycleState.Failed;
        }
        SetControllerVisible(false);
        RuntimeError.Visibility = Visibility.Visible;
        RuntimeErrorText.Text = _localizer.Get("ReaderHost_RuntimeMissing");
    }

    private void ShowContentError()
    {
        SetControllerVisible(false);
        RuntimeError.Visibility = Visibility.Visible;
        RuntimeErrorText.Text = _localizer.Get("ReaderHost_ContentUnavailable");
    }

    private static async Task<IRandomAccessStream> CreateStreamAsync(ReadOnlyMemory<byte> bytes)
    {
        InMemoryRandomAccessStream stream = new();
        using DataWriter writer = new(stream);
        writer.WriteBytes(bytes.ToArray());
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    private bool ResolveApplicationDark(CoreApplicationTheme theme) =>
        theme switch
        {
            CoreApplicationTheme.Dark => true,
            CoreApplicationTheme.Light => false,
            _ => ActualTheme == ElementTheme.Dark
        };

    private void ApplyColorScheme(bool dark)
    {
        if (Core is not { } core)
        {
            return;
        }

        BrowserSurface.DefaultBackgroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 17, 20, 24)
            : Microsoft.UI.Colors.White;
        core.Profile.PreferredColorScheme = dark
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;
    }

    private void ApplyZoom()
    {
        lock (_zoomSync)
        {
            _zoomVersion++;
            if (_zoomApplyRunning ||
                _lifecycleState != ReaderWebViewLifecycleState.Ready)
            {
                return;
            }

            _zoomApplyRunning = true;
        }

        _ = ApplyZoomLoopAsync();
    }

    public async Task ApplyThemeAsync(
        CoreApplicationTheme theme,
        bool? systemDark = null)
    {
        bool effectiveDark = theme switch
        {
            CoreApplicationTheme.Dark => true,
            CoreApplicationTheme.Light => false,
            _ => systemDark ?? ActualTheme == ElementTheme.Dark
        };

        bool changed = !_themeInitialized ||
                       effectiveDark != _effectiveApplicationDark;
        _effectiveApplicationDark = effectiveDark;
        _themeInitialized = true;
        ApplyColorScheme(effectiveDark);
        if (!_dynamicThemeEnabled ||
            !changed ||
            _lifecycleState != ReaderWebViewLifecycleState.Ready ||
            Core is not { } core ||
            _document is not { } document)
        {
            return;
        }

        await _themeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_lifecycleState != ReaderWebViewLifecycleState.Ready ||
                Core != core ||
                _document?.RenderToken != document.RenderToken)
            {
                return;
            }

            string darkJson = JsonSerializer.Serialize(_effectiveApplicationDark);
            string tokenJson = JsonSerializer.Serialize(document.RenderToken);
            Interlocked.Increment(ref _scriptExecutionCount);
            _ = await core.ExecuteScriptAsync(
                $"window.vdictApplyTheme?.({darkJson},{tokenJson}); true");
        }
        catch (Exception exception)
            when (exception is COMException or InvalidOperationException)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "webview-theme",
                $"Render={RenderCorrelation(document)}; DynamicThemeFailed={exception.GetType().Name}");
        }
        finally
        {
            _themeGate.Release();
        }
    }

    private async Task ApplyZoomLoopAsync()
    {
        try
        {
            while (_lifecycleState == ReaderWebViewLifecycleState.Ready)
            {
                await Task.Delay(24).ConfigureAwait(true);
                double requested;
                long version;
                lock (_zoomSync)
                {
                    requested = _requestedZoom;
                    version = _zoomVersion;
                }

                if (Core is { } core)
                {
                    string zoom = requested.ToString("0.###", CultureInfo.InvariantCulture);
                    Interlocked.Increment(ref _scriptExecutionCount);
                    await core.ExecuteScriptAsync(
                        $"document.documentElement.style.zoom='{zoom}'; " +
                        "window.vdictRequestFrameSizes?.(); true");
                }

                lock (_zoomSync)
                {
                    if (version != _zoomVersion)
                    {
                        continue;
                    }

                    _zoomApplyRunning = false;
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "reader-zoom",
                "无法应用正文缩放。",
                exception);
        }
        finally
        {
            lock (_zoomSync)
            {
                _zoomApplyRunning = false;
            }
        }
    }

    private static string? Header(
        CoreWebView2WebResourceRequestedEventArgs args,
        string name) =>
        args.Request.Headers.Contains(name)
            ? args.Request.Headers.GetHeader(name)
            : null;

    private static string BuildResponseHeaders(ReaderResourceResponse response)
    {
        StringBuilder headers = new();
        headers.Append("Content-Type: ").Append(response.MimeType).Append("\r\n");
        foreach ((string name, string value) in response.Headers)
        {
            headers.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        return headers.ToString();
    }

    private static string RenderCorrelation(ReaderDocument? document) =>
        document is null || string.IsNullOrEmpty(document.RenderToken)
            ? "none"
            : document.RenderToken[..Math.Min(document.RenderToken.Length, 8)];

    private void WriteRenderPipelineDiagnostics(
        ReaderRenderStage stage,
        ReaderDocument document,
        long version)
    {
        switch (stage)
        {
            case ReaderRenderStage.NavigationGateWait:
                WriteRenderDiagnostics(
                    "navigation-gate-wait",
                    document,
                    version,
                    recordTimeline: false);
                break;
            case ReaderRenderStage.NavigationGateAcquired:
                WriteRenderDiagnostics(
                    "navigation-gate-acquired",
                    document,
                    version,
                    recordTimeline: false);
                break;
            case ReaderRenderStage.UnavailableBeforeStop:
                WriteRenderDiagnostics(
                    "render-skip",
                    document,
                    version,
                    Core is null ? "core-missing-before-stop" : "lifecycle-not-ready",
                    recordTimeline: false);
                break;
            case ReaderRenderStage.StopMediaStart:
                WriteRenderDiagnostics(
                    "stop-media-start",
                    document,
                    version,
                    recordTimeline: false);
                break;
            case ReaderRenderStage.StopMediaComplete:
                WriteRenderDiagnostics(
                    "stop-media-complete",
                    document,
                    version,
                    recordTimeline: false);
                break;
            case ReaderRenderStage.CancelledAfterStop:
                WriteRenderDiagnostics(
                    "render-skip",
                    document,
                    version,
                    "cancelled-after-stop",
                    recordTimeline: false);
                break;
            case ReaderRenderStage.UnavailableAfterStop:
                WriteRenderDiagnostics(
                    "render-skip",
                    document,
                    version,
                    Core is null ? "core-missing-after-stop" : "lifecycle-not-ready",
                    recordTimeline: false);
                break;
            case ReaderRenderStage.StaleOrDuplicateAfterStop:
                WriteRenderDiagnostics(
                    "render-skip",
                    document,
                    version,
                    "stale-or-duplicate-after-stop",
                    recordTimeline: false);
                break;
            case ReaderRenderStage.RenderClaimed:
                WriteRenderDiagnostics(
                    "render-claimed",
                    document,
                    version,
                    recordTimeline: false);
                break;
        }
    }

    private void WriteRenderDiagnostics(
        string stage,
        ReaderDocument? document,
        long version,
        string? skipReason = null,
        bool recordTimeline = true)
    {
        if (recordTimeline)
        {
            DebugLookupProbe.Record(
                $"Render:{stage}",
                renderToken: document?.RenderToken,
                detail:
                    $"Version={version}; SkipReason={skipReason ?? "none"}");
        }

        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            return;
        }

        _logger.Write(
            AppLogLevel.Information,
            "webview-render-pipeline",
            $"Stage={stage}; Version={version}; LatestVersion={_navigationState.LatestVersion}; " +
            $"LastRenderedVersion={_navigationState.LastRenderedVersion}; " +
            $"Render={RenderCorrelation(document)}; Lifecycle={_lifecycleState}; " +
            $"CorePresent={Core is not null}; HasUsableLayout={HasUsableLayout()}; " +
            $"Thread={System.Environment.CurrentManagedThreadId}; " +
            $"SkipReason={skipReason ?? "none"}");
    }

    private double RenderElapsedMilliseconds() =>
        _renderStartedTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_renderStartedTimestamp).TotalMilliseconds;

    private void WriteRenderTimeline(
        string stage,
        double stageCpuMilliseconds,
        int bytes)
    {
        DebugLookupProbe.Record(
            stage,
            renderToken: _document?.RenderToken,
            detail:
                $"RenderElapsedMs={RenderElapsedMilliseconds():F3}; " +
                $"StageBuildMs={stageCpuMilliseconds:F3}; Bytes={bytes}");
        if (!_catalog.GetSettingsSnapshot().CompatibilityDiagnosticsEnabled)
        {
            return;
        }

        string message = FormattableString.Invariant(
            $"""Schema=reader-timeline-v1; Render={RenderCorrelation(_document)}; Stage={stage}; RenderElapsedMs={RenderElapsedMilliseconds():F3}; StageBuildMs={stageCpuMilliseconds:F3}; Bytes={bytes}; NavigationSubmits={DiagnosticNavigationSubmitCount}; ScriptExecutions={DiagnosticScriptExecutionCount}; Thread={System.Environment.CurrentManagedThreadId}""");
        _logger.Write(
            AppLogLevel.Information,
            "reader-timeline",
            message);
    }

    internal async Task<ReaderRenderCompletion> WaitForRenderAsync(
        string renderToken,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.Elapsed < timeout)
        {
            if (_renderCompletionToken.Equals(renderToken, StringComparison.Ordinal) &&
                _renderCompletion is { } completion)
            {
                return await completion.Task.WaitAsync(timeout - wait.Elapsed, cancellationToken)
                    .ConfigureAwait(true);
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException(
            $"Render {renderToken[..Math.Min(renderToken.Length, 8)]} did not start within {timeout}.");
    }

    internal Task CaptureBaselinePreviewAsync(string path) =>
        CaptureReaderPreviewAsync(path);

    internal async Task<string> ExecuteInFixtureFrameAsync(
        string requiredElementId,
        string expression)
    {
        string idJson = JsonSerializer.Serialize(requiredElementId);
        foreach (CoreWebView2Frame frame in _frames.ToArray())
        {
            try
            {
                string hasElement = await frame.ExecuteScriptAsync(
                    $"Boolean(document.getElementById({idJson}))");
                Interlocked.Increment(ref _scriptExecutionCount);
                if (!hasElement.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Interlocked.Increment(ref _scriptExecutionCount);
                return await frame.ExecuteScriptAsync(expression);
            }
            catch (COMException)
            {
            }
        }

        throw new InvalidOperationException(
            $"No active fixture frame contains element '{requiredElementId}'.");
    }

    internal async Task<string> ExecuteInFirstFrameAsync(string expression)
    {
        foreach (CoreWebView2Frame frame in _frames.ToArray())
        {
            try
            {
                Interlocked.Increment(ref _scriptExecutionCount);
                return await frame.ExecuteScriptAsync(expression);
            }
            catch (COMException)
            {
            }
        }

        throw new InvalidOperationException("No active reader frame is available.");
    }

    internal async Task<string> ExecuteInShellAsync(string expression)
    {
        CoreWebView2 core = Core ?? throw new InvalidOperationException(
            "WebView2 is not ready.");
        Interlocked.Increment(ref _scriptExecutionCount);
        return await core.ExecuteScriptAsync(expression);
    }

    internal async Task DispatchTrustedClickAsync(string requiredElementId)
    {
        CoreWebView2 core = Core ?? throw new InvalidOperationException("WebView2 is not ready.");
        _ = await ExecuteInFixtureFrameAsync(
            requiredElementId,
            $"(()=>{{const element=document.getElementById(" +
            $"{JsonSerializer.Serialize(requiredElementId)});" +
            "element.scrollIntoView({block:'center',inline:'center'});return true;})()");
        await Task.Delay(100);
        string targetRectJson = await ExecuteInFixtureFrameAsync(
            requiredElementId,
            $"JSON.stringify(document.getElementById({JsonSerializer.Serialize(requiredElementId)})" +
            ".getBoundingClientRect().toJSON())");
        Interlocked.Increment(ref _scriptExecutionCount);
        string frameRectJson = await core.ExecuteScriptAsync(
            "JSON.stringify(document.querySelector('iframe.entry').getBoundingClientRect().toJSON())");
        using JsonDocument targetJson = JsonDocument.Parse(
            JsonSerializer.Deserialize<string>(targetRectJson) ?? "{}");
        using JsonDocument frameJson = JsonDocument.Parse(
            JsonSerializer.Deserialize<string>(frameRectJson) ?? "{}");
        double x = Number(frameJson.RootElement, "x") +
                   Number(targetJson.RootElement, "x") +
                   Math.Max(1, Number(targetJson.RootElement, "width") / 2);
        double y = Number(frameJson.RootElement, "y") +
                   Number(targetJson.RootElement, "y") +
                   Math.Max(1, Number(targetJson.RootElement, "height") / 2);
        string pressed = JsonSerializer.Serialize(new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        string released = JsonSerializer.Serialize(new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        _ = await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pressed);
        _ = await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", released);
    }

    private long GetWebViewProcessMemoryBytes()
    {
        try
        {
            return _environment?.GetProcessInfos()
                .Select(info =>
                {
                    try
                    {
                        using Process process = Process.GetProcessById(info.ProcessId);
                        return process.PrivateMemorySize64;
                    }
                    catch (ArgumentException)
                    {
                        return 0;
                    }
                })
                .Sum() ?? 0;
        }
        catch (COMException)
        {
            return 0;
        }
    }

    private static double Number(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDouble(out double number)
            ? number
            : 0;

    private static string WebMessageString(
        JsonElement root,
        string propertyName,
        int maximumLength) =>
        DictionaryWebMessageValidator.TryGetBoundedString(
            root,
            propertyName,
            maximumLength,
            out string value)
            ? value
            : string.Empty;

    private static bool WebMessageBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static double WebMessageNumber(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) &&
        value.TryGetDouble(out double number) &&
        double.IsFinite(number)
            ? number
            : 0;

    private static ReaderDarkModeAssets? LoadDarkModeAssets()
    {
        try
        {
            string scriptPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Scripts",
                "darkreader.js");
            string overridesPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Styles",
                "dark-mode-overrides.css");
            return File.Exists(scriptPath)
                ? new ReaderDarkModeAssets(
                    File.ReadAllText(scriptPath),
                    File.Exists(overridesPath)
                        ? File.ReadAllText(overridesPath)
                        : string.Empty)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadWelcomeLogo()
    {
        try
        {
            string logoPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "logo_rect.png");
            return File.Exists(logoPath) ? File.ReadAllBytes(logoPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ResourceMemoryProbe : IAsyncDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _sampling;
        private readonly long _managedHeapBaseline;
        private readonly long _workingSetBaseline;
        private long _managedHeapPeak;
        private long _workingSetPeak;
        private int _stopped;

        public ResourceMemoryProbe()
        {
            (_managedHeapBaseline, _workingSetBaseline) = ReadMemory();
            _managedHeapPeak = _managedHeapBaseline;
            _workingSetPeak = _workingSetBaseline;
            _sampling = Task.Run(SampleAsync);
        }

        public async Task<ResourceMemoryObservation> StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _cancellation.Cancel();
                try
                {
                    await _sampling.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                Observe();
            }

            return new ResourceMemoryObservation(
                Math.Max(0, _managedHeapPeak - _managedHeapBaseline),
                Math.Max(0, _workingSetPeak - _workingSetBaseline));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _cancellation.Dispose();
            _process.Dispose();
        }

        private async Task SampleAsync()
        {
            while (true)
            {
                await Task.Delay(2, _cancellation.Token).ConfigureAwait(false);
                Observe();
            }
        }

        private void Observe()
        {
            (long managedHeap, long workingSet) = ReadMemory();
            _managedHeapPeak = Math.Max(_managedHeapPeak, managedHeap);
            _workingSetPeak = Math.Max(_workingSetPeak, workingSet);
        }

        private (long ManagedHeap, long WorkingSet) ReadMemory()
        {
            _process.Refresh();
            return (GC.GetTotalMemory(forceFullCollection: false), _process.WorkingSet64);
        }
    }

    private sealed class WebView2RuntimeNotFoundException : InvalidOperationException
    {
        public WebView2RuntimeNotFoundException()
            : base("未检测到 Evergreen WebView2 Runtime。")
        {
        }

        public WebView2RuntimeNotFoundException(string message)
            : base(message)
        {
        }
    }

    private sealed class VisibilitySuppression(ReaderWebViewHost owner) : IDisposable
    {
        private ReaderWebViewHost? _owner = owner;

        public void Dispose()
        {
            ReaderWebViewHost? target = Interlocked.Exchange(ref _owner, null);
            target?.ReleaseVisibilitySuppression();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Math.Max(0, Right - Left);

        public readonly int Height => Math.Max(0, Bottom - Top);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out NativeRect rect);
}

internal readonly record struct ResourceMemoryObservation(
    long ManagedHeapPeakDelta,
    long WorkingSetPeakDelta)
{
    public static ResourceMemoryObservation Empty { get; } = new(0, 0);
}

internal sealed record ReaderRenderCompletion(
    string RenderToken,
    int FrameCount,
    double FirstFrameReadyMilliseconds,
    double AllFramesReadyMilliseconds,
    long ProcessWorkingSetBytes,
    long WebViewProcessMemoryBytes);
