using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Infrastructure.Catalog;
using VDict.Infrastructure.Environment;
using VDict.Infrastructure.Indexing;
using VDict.Infrastructure.Maintenance;
using VDict.Infrastructure.Settings;
using VDict.Presentation.Services;
using VDict.Presentation.Settings;
using VDict.Presentation.ViewModels;
using VDict.WinUI.Controls;
using VDict.WinUI.Diagnostics;
using VDict.WinUI.Localization;
using VDict.WinUI.Platform;
using VDict.WinUI.Views;

namespace VDict.WinUI;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private readonly IAppLogger _logger;
    private readonly IAppLocalizer _localizer;
    private readonly ResourceManager? _resourceManager;
    private Window? _mainWindow;

    public App()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        IAppEnvironment environment = WindowsAppEnvironment.Create();
        IAppDataPaths paths = WindowsAppEnvironment.CreatePaths(environment);
        ApplicationLanguage startupLanguage = StartupLanguageReader.Read(paths);
        ApplicationLanguage effectiveLanguage = ApplicationLanguageResolver.Resolve(
            startupLanguage,
            CultureInfo.CurrentUICulture.Name);
        if (!environment.IsPackaged)
        {
            _resourceManager = new ResourceManager(MrtResourcePath.For(environment));
            ResourceManagerRequested += App_ResourceManagerRequested;
        }

        ApplicationLanguages.PrimaryLanguageOverride = effectiveLanguage switch
        {
            ApplicationLanguage.ChineseSimplified => "zh-CN",
            _ => "en-US"
        };

        InitializeComponent();
        ServiceCollection services = new();
        services.AddSingleton(environment);
        services.AddSingleton(paths);
        services.AddSingleton<DiagnosticsPrivacySettings>();
        services.AddSingleton<WindowsCapabilities>();
        services.AddSingleton<IAppLocalizer, MrtAppLocalizer>();
        services.AddSingleton<MainWindowAccessor>();
        services.AddSingleton<AtomicSettingsStore>();
        services.AddSingleton<IFontCatalogService, WindowsFontCatalogService>();
        services.AddSingleton<SessionLog>();
        services.AddSingleton<IAppLogger, DiagnosticsLogWriter>();
        services.AddSingleton<DictionaryIndexBuilder>();
        services.AddSingleton<DictionaryFileInspector>();
        services.AddSingleton<CacheMaintenanceService>();
        services.AddSingleton<DictionaryCatalog>();
        services.AddSingleton<IDictionaryCatalog>(
            provider => provider.GetRequiredService<DictionaryCatalog>());
        services.AddSingleton<IExternalLinkService, ExternalLinkService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IUiDispatcher>(
            _ => new UiDispatcher(
                DispatcherQueue.GetForCurrentThread() ??
                throw new InvalidOperationException("当前线程没有 UI DispatcherQueue。")));
        services.AddSingleton<IModalWindowService, ModalWindowService>();
        services.AddSingleton<ReaderViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ReaderWebViewHost>();
        services.AddSingleton<MainWindow>();
#if DEBUG
        services.AddSingleton<DebugTimelineWindowController>();
#endif

        services.AddScoped<PickerWindowContext>();
        services.AddScoped<IFilePickerService, FilePickerService>();
        services.AddScoped<WindowDialogService>();
        services.AddScoped<IDialogService>(
            provider => provider.GetRequiredService<WindowDialogService>());
        services.AddScoped<SettingsEditSession>(provider =>
        {
            IDictionaryCatalog catalog = provider.GetRequiredService<IDictionaryCatalog>();
            return new SettingsEditSession(
                catalog.GetSettingsSnapshot(),
                catalog,
                provider.GetRequiredService<IAppLocalizer>());
        });
        services.AddScoped<SettingsWindowViewModel>();
        services.AddScoped<SettingsWindow>();

        _services = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
        _logger = _services.GetRequiredService<IAppLogger>();
        _localizer = _services.GetRequiredService<IAppLocalizer>();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) =>
        _ = LaunchAsync();

    private void App_ResourceManagerRequested(
        object sender,
        ResourceManagerRequestedEventArgs args)
    {
        if (_resourceManager is not null)
        {
            args.CustomResourceManager = _resourceManager;
        }
    }

    private async Task LaunchAsync()
    {
        MainWindow? mainWindow = null;
        try
        {
            mainWindow = _services.GetRequiredService<MainWindow>();
            _mainWindow = mainWindow;
            mainWindow.Closed += MainWindow_Closed;
            mainWindow.Activate();
#if DEBUG
            _services
                .GetRequiredService<DebugTimelineWindowController>()
                .Show(mainWindow);
#endif
            IAppDataPaths paths = _services.GetRequiredService<IAppDataPaths>();
            paths.EnsureCreated();
            DictionaryCatalog catalog = _services.GetRequiredService<DictionaryCatalog>();
            SettingsLoadResult load = await catalog.InitializeAsync().ConfigureAwait(true);
            mainWindow.ViewModel.Reader.MarkCatalogReady();
            mainWindow.ApplyInitializedSettings(load.Settings);
            if (!string.IsNullOrWhiteSpace(load.RecoveryMessage))
            {
                mainWindow.ViewModel.Reader.StatusMessage = load.RecoveryMessage;
            }

            string? startupLookup = GetStartupLookup();
            if (!string.IsNullOrWhiteSpace(startupLookup))
            {
                await mainWindow.ViewModel.Reader.LookupAsync(startupLookup).ConfigureAwait(true);
            }

            string? baselineOutput = GetCommandLineValue("--remediation-baseline-output");
            if (!string.IsNullOrWhiteSpace(baselineOutput))
            {
                int samples = int.TryParse(
                    GetCommandLineValue("--remediation-baseline-samples"),
                    out int parsedSamples)
                    ? Math.Clamp(parsedSamples, 1, 100)
                    : 30;
                ReaderWebViewHost readerHost =
                    _services.GetRequiredService<ReaderWebViewHost>();
                RemediationBaselineRunner runner = new(
                    mainWindow.ViewModel.Reader,
                    readerHost,
                    catalog,
                    mainWindow);
                try
                {
                    await runner.RunAsync(baselineOutput, samples).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    string failurePath = Path.GetFullPath(baselineOutput) + ".failure.txt";
                    Directory.CreateDirectory(Path.GetDirectoryName(failurePath)!);
                    await File.WriteAllTextAsync(failurePath, exception.ToString())
                        .ConfigureAwait(true);
                    throw;
                }
                finally
                {
                    mainWindow.Close();
                }

                return;
            }

            if (HasCommandLineSwitch("--diagnostic-crash-browser"))
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                ReaderWebViewHost readerHost =
                    _services.GetRequiredService<ReaderWebViewHost>();
                await readerHost.TriggerBrowserProcessFailureForDiagnosticsAsync()
                    .ConfigureAwait(true);
            }
        }
        catch (PortableDataDirectoryNotWritableException exception)
        {
            _logger.Write(AppLogLevel.Error, "startup", exception.Message, exception);
            if (mainWindow is not null)
            {
                await mainWindow.ShowFatalErrorAsync(
                    _localizer.Get("App_PortableDataDirectoryNotWritable"))
                    .ConfigureAwait(true);
                mainWindow.Close();
            }
        }
        catch (Exception exception)
        {
            _logger.Write(AppLogLevel.Error, "startup", "启动初始化失败。", exception);
            mainWindow?.ViewModel.Reader.MarkCatalogFailed(
                _localizer.Get("App_StartupInitializationFailed"));
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            if (sender is MainWindow mainWindow)
            {
                await mainWindow.FlushCandidatePaneWidthAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.Write(
                AppLogLevel.Warning,
                "shutdown",
                "关闭时保存候选栏宽度失败；将继续释放应用资源。",
                exception);
        }
        finally
        {
            if (sender is Window window)
            {
                window.Closed -= MainWindow_Closed;
            }

            _mainWindow = null;
            try
            {
                await _services.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _logger.Write(
                    AppLogLevel.Error,
                    "shutdown",
                    "释放应用服务失败。",
                    exception);
            }
        }
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger.Write(AppLogLevel.Error, "ui", "发生未处理的 WinUI 异常。", e.Exception);
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }

    private static string? GetStartupLookup()
        => GetCommandLineValue("--lookup");

    private static string? GetCommandLineValue(string name)
    {
        string[] arguments = System.Environment.GetCommandLineArgs();
        for (int index = 1; index < arguments.Length; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length)
            {
                return arguments[index + 1].Trim();
            }

            string prefix = name + "=";
            if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index][prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static bool HasCommandLineSwitch(string value) =>
        System.Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => argument.Equals(value, StringComparison.OrdinalIgnoreCase));
}
