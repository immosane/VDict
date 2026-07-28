using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Presentation.Settings;
using VDict.Presentation.ViewModels;
using VDict.WinUI.Controls;
using VDict.WinUI.Views;

namespace VDict.WinUI.Diagnostics;

internal sealed class RemediationBaselineRunner(
    ReaderViewModel reader,
    ReaderWebViewHost readerHost,
    IDictionaryCatalog catalog,
    MainWindow mainWindow)
{
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(20);
    private readonly List<string> _statusEvents = [];

    public async Task RunAsync(
        string outputPath,
        int samples,
        CancellationToken cancellationToken = default)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("Baseline output path has no directory.");
        Directory.CreateDirectory(outputDirectory);
        string screenshots = Path.Combine(outputDirectory, "screenshots");
        Directory.CreateDirectory(screenshots);
        readerHost.StatusChanged += ReaderHost_StatusChanged;
        UiStallMonitor stallMonitor = new(DispatcherQueue.GetForCurrentThread());
        stallMonitor.Start();
        Stopwatch runTimer = Stopwatch.StartNew();
        try
        {
            List<BaselineQuerySample> querySamples = [];
            querySamples.Add(await QueryAsync(
                "perf-small-cold",
                "fixture-perf-small",
                "process-cold/store-initialized/webview-first-content",
                cancellationToken));
            for (int index = 0; index < samples; index++)
            {
                querySamples.Add(await QueryAsync(
                    "perf-small-warm",
                    "fixture-perf-small",
                    "same-process/same-store/same-webview",
                    cancellationToken));
                querySamples.Add(await QueryAsync(
                    "perf-large-warm",
                    "fixture-perf-large",
                    "same-process/same-store/same-webview",
                    cancellationToken));
            }

            string[] compatibilityQueries =
            [
                "fixture-basic",
                "fixture-multi-entry",
                "fixture-css",
                "fixture-script-standard",
                "fixture-script-legacy",
                "fixture-image",
                "fixture-audio",
                "fixture-security",
                "fixture-isolation",
                "fixture-malformed"
            ];
            List<BaselineQuerySample> compatibility = [];
            foreach (string query in compatibilityQueries)
            {
                compatibility.Add(await QueryAsync(
                    "compatibility",
                    query,
                    "same-process/single-pass",
                    cancellationToken));
            }

            BaselineRapidLookupResult rapidLookup = await RunRapidLookupAsync(
                samples,
                cancellationToken);
            BaselineInteractionResult interactions = await RunInteractionsAsync(
                cancellationToken);
            BaselineThemeResult themes = await RunThemesAsync(
                screenshots,
                samples,
                cancellationToken);
            BaselineZoomResult zoom = await RunZoomAsync(cancellationToken);
            BaselineSettingsConcurrencyResult settingsConcurrency =
                await RunSettingsConcurrencyAsync(cancellationToken);
            IReadOnlyList<BaselineCatalogMutation> catalogMutations =
                await RunCatalogMutationMatrixAsync(cancellationToken);
            stallMonitor.Stop();
            runTimer.Stop();

            using Process process = Process.GetCurrentProcess();
            BaselineReport report = new(
                "reader-baseline-v1",
                DateTimeOffset.UtcNow,
                new BaselineEnvironment(
                    System.Environment.OSVersion.VersionString,
                    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    System.Environment.ProcessorCount,
                    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    Environment.Is64BitProcess,
                    readerHost.WebViewRuntimeVersion,
                    "Release x64 portable",
                    "not-detected",
                    "not-controlled",
                    "isolated-portable/Data",
                    "isolated-portable/Data/WebView2"),
                samples,
                querySamples,
                compatibility,
                rapidLookup,
                interactions,
                themes,
                zoom,
                settingsConcurrency,
                catalogMutations,
                new BaselineResources(
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    GC.GetTotalAllocatedBytes(precise: true),
                    reader.DiagnosticBackgroundBuildConcurrencyPeak,
                    stallMonitor.MaximumStallMilliseconds,
                    runTimer.Elapsed.TotalMilliseconds),
                _statusEvents.ToArray());
            string json = JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                fullOutputPath,
                json + "\n",
                cancellationToken);
        }
        finally
        {
            stallMonitor.Stop();
            readerHost.StatusChanged -= ReaderHost_StatusChanged;
        }
    }

    private async Task<BaselineRapidLookupResult> RunRapidLookupAsync(
        int bursts,
        CancellationToken cancellationToken)
    {
        const int inputsPerBurst = 10;
        int maxStartedPerBurst = 0;
        int cancellationStart =
            reader.DiagnosticCancellationObservationMilliseconds.Length;
        for (int burst = 0; burst < bursts; burst++)
        {
            long lookupStart = reader.DiagnosticLookupExecutionCount;
            List<Task> lookups =
            [
                reader.LookupAsync(
                    "fixture-perf-large",
                    recordHistory: false,
                    cancellationToken: cancellationToken,
                    origin: LookupOrigin.Candidate)
            ];
            await Task.Delay(1, cancellationToken);
            for (int input = 1; input < inputsPerBurst; input++)
            {
                string query = input == inputsPerBurst - 1
                    ? "fixture-basic"
                    : "fixture-perf-large";
                lookups.Add(reader.LookupAsync(
                    query,
                    recordHistory: false,
                    cancellationToken: cancellationToken,
                    origin: LookupOrigin.Candidate));
            }

            await Task.WhenAll(lookups);
            maxStartedPerBurst = Math.Max(
                maxStartedPerBurst,
                checked((int)(
                    reader.DiagnosticLookupExecutionCount - lookupStart)));
            ReaderDocument document = reader.Document
                ?? throw new InvalidOperationException(
                    "Rapid lookup did not produce a final document.");
            _ = await readerHost.WaitForRenderAsync(
                document.RenderToken,
                RenderTimeout,
                cancellationToken);
        }

        double[] observations = reader
            .DiagnosticCancellationObservationMilliseconds
            .Skip(cancellationStart)
            .Order()
            .ToArray();
        return new BaselineRapidLookupResult(
            bursts,
            inputsPerBurst,
            maxStartedPerBurst,
            observations.Length,
            Percentile(observations, 0.5),
            Percentile(observations, 0.95),
            observations.Length == 0 ? 0 : observations[^1],
            reader.CurrentWord,
            reader.Document?.RenderToken ?? string.Empty);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * percentile) - 1,
            0,
            sorted.Length - 1);
        return sorted[index];
    }

    private async Task<BaselineQuerySample> QueryAsync(
        string scenario,
        string query,
        string cacheState,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        long allocationStart = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        await reader.LookupAsync(
            query,
            recordHistory: false,
            cancellationToken,
            LookupOrigin.Manual);
        ReaderDocument document = reader.Document
            ?? throw new InvalidOperationException($"Query '{query}' did not produce a document.");
        ReaderRenderCompletion completion = await readerHost.WaitForRenderAsync(
            document.RenderToken,
            RenderTimeout,
            cancellationToken);
        timer.Stop();
        return new BaselineQuerySample(
            scenario,
            query,
            cacheState,
            document.RenderToken[..8],
            document.Frames.Length,
            timer.Elapsed.TotalMilliseconds,
            completion.FirstFrameReadyMilliseconds,
            completion.AllFramesReadyMilliseconds,
            document.BuildDiagnostics?.EntryRewriteMilliseconds ?? 0,
            document.BuildDiagnostics?.DocumentAssemblyMilliseconds ?? 0,
            document.BuildDiagnostics?.TotalMilliseconds ?? 0,
            (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds,
            Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocationStart),
            completion.ProcessWorkingSetBytes,
            completion.WebViewProcessMemoryBytes);
    }

    private async Task<BaselineInteractionResult> RunInteractionsAsync(
        CancellationToken cancellationToken)
    {
        await QueryAsync(
            "interaction-image",
            "fixture-image",
            "same-process",
            cancellationToken);
        string imageBefore = DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
            "custom-viewer",
            "JSON.stringify({images:Array.from(document.images).map(i=>({complete:i.complete," +
            "width:i.naturalWidth,src:i.getAttribute('src')})),viewer:document.body.dataset.viewer||''})"));
        string imageAfter = string.Empty;
        for (int retry = 0; retry < 3 && !imageAfter.Contains(
                 "\"viewer\":\"open\"",
                 StringComparison.Ordinal); retry++)
        {
            await readerHost.DispatchTrustedClickAsync("custom-viewer");
            await Task.Delay(150, cancellationToken);
            imageAfter = DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
                "custom-viewer",
                "JSON.stringify({viewer:document.body.dataset.viewer||'',active:document.activeElement?.id||''})"));
        }
        if (!imageAfter.Contains("\"viewer\":\"open\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Image viewer did not observe a trusted click.");
        }

        await QueryAsync(
            "interaction-audio",
            "fixture-audio",
            "same-process",
            cancellationToken);
        _ = await readerHost.ExecuteInFixtureFrameAsync(
            "script-audio",
            """
            (()=>{const audio=document.querySelector('#audio-element');
              audio.pause();audio.preload='none';
              audio.src=document.querySelector('#plain-audio').href;audio.load();
              window.__vdictBaselineAudio={records:[]};
              document.querySelector('#script-audio').addEventListener('click',async()=>{
                const started=performance.now();
                const record={sequence:window.__vdictBaselineAudio.records.length+1,
                  promise:'pending',clickToPromiseMs:null,clickToPlayingMs:null};
                window.__vdictBaselineAudio.records.push(record);
                audio.addEventListener('playing',()=>{
                  record.clickToPlayingMs=performance.now()-started;
                },{once:true});
                try{await audio.play();record.promise='resolved';
                  record.clickToPromiseMs=performance.now()-started;}
                catch(e){record.promise=e.name||'rejected';
                  record.clickToPromiseMs=performance.now()-started;}
              });return true;})()
            """);
        for (int audioAttempt = 0; audioAttempt < 3; audioAttempt++)
        {
            int expectedRecords = audioAttempt + 1;
            int observedRecords = 0;
            for (int retry = 0; retry < 3 && observedRecords < expectedRecords; retry++)
            {
                await readerHost.DispatchTrustedClickAsync("script-audio");
                await Task.Delay(audioAttempt == 0 ? 900 : 500, cancellationToken);
                string count = await readerHost.ExecuteInFixtureFrameAsync(
                    "script-audio",
                    "window.__vdictBaselineAudio.records.length");
                _ = int.TryParse(
                    count.Trim('"'),
                    out observedRecords);
            }
            if (observedRecords < expectedRecords)
            {
                throw new InvalidOperationException(
                    $"Audio trusted click {expectedRecords} was not observed.");
            }
            if (audioAttempt < 2)
            {
                _ = await readerHost.ExecuteInFixtureFrameAsync(
                    "script-audio",
                    "(()=>{const a=document.querySelector('#audio-element');" +
                    "a.pause();a.currentTime=0;return true;})()");
            }
        }
        string audioState = DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
            "script-audio",
            "JSON.stringify({...window.__vdictBaselineAudio," +
            "paused:document.querySelector('#audio-element').paused," +
            "currentTime:document.querySelector('#audio-element').currentTime})"));

        await QueryAsync(
            "interaction-security",
            "fixture-security",
            "same-process",
            cancellationToken);
        string securityToken = reader.Document?.RenderToken ?? string.Empty;
        _ = await readerHost.ExecuteInFixtureFrameAsync(
            "fixture-security-marker",
            """
            (()=>{const external=document.querySelector('a[target="_blank"]');
              const download=document.querySelector('a[download]');
              if(external){external.id='baseline-external-link';
                external.addEventListener('click',()=>external.dataset.clicked='true');}
              if(download){download.id='baseline-download-link';
                download.addEventListener('click',()=>download.dataset.clicked='true');}
              return Boolean(external&&download);})()
            """);
        List<string> securityEvents = [];
        void CaptureSecurityEvent(object? sender, string message) =>
            securityEvents.Add(message);
        readerHost.StatusChanged += CaptureSecurityEvent;
        try
        {
            await readerHost.DispatchTrustedClickAsync("baseline-external-link");
            await Task.Delay(100, cancellationToken);
            await readerHost.DispatchTrustedClickAsync("baseline-download-link");
            await Task.Delay(250, cancellationToken);
        }
        finally
        {
            readerHost.StatusChanged -= CaptureSecurityEvent;
        }
        string securityState = DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
            "fixture-security-marker",
            "JSON.stringify({location:location.href,externalAnchors:" +
            "document.querySelectorAll('a[href^=\"http\"]').length," +
            "downloads:document.querySelectorAll('a[download]').length," +
            "externalClicked:document.querySelector('#baseline-external-link')?.dataset.clicked||''," +
            "downloadClicked:document.querySelector('#baseline-download-link')?.dataset.clicked||''})"));

        return new BaselineInteractionResult(
            imageBefore,
            imageAfter,
            audioState,
            securityState,
            securityEvents.ToArray(),
            securityToken == reader.Document?.RenderToken);
    }

    private async Task<BaselineThemeResult> RunThemesAsync(
        string screenshotDirectory,
        int warmSamples,
        CancellationToken cancellationToken)
    {
        await QueryAsync(
            "theme-state",
            "fixture-image",
            "same-process",
            cancellationToken);
        string before = string.Empty;
        for (int retry = 0; retry < 3 && !before.Contains(
                 "\"viewer\":\"open\"",
                 StringComparison.Ordinal); retry++)
        {
            await readerHost.DispatchTrustedClickAsync("custom-viewer");
            await Task.Delay(150, cancellationToken);
            before = DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
                "custom-viewer",
                "JSON.stringify({viewer:document.body.dataset.viewer||'',scrollY,script:" +
                "Boolean(window.fixtureExternal),media:document.querySelectorAll('audio').length})"));
        }
        if (!before.Contains("\"viewer\":\"open\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Theme state baseline could not open the image viewer.");
        }
        List<string> screenshots = [];
        List<string> states = [];
        List<BaselineThemeSample> themeSamples = [];
        foreach (ApplicationTheme theme in new[]
                 {
                     ApplicationTheme.Light,
                     ApplicationTheme.Dark,
                     ApplicationTheme.System
                 })
        {
            string previousToken = reader.Document?.RenderToken ?? string.Empty;
            await CommitThemeAsync(theme, cancellationToken);
            await WaitForDynamicThemeAsync(previousToken, cancellationToken);
            if (reader.Document?.RenderToken != previousToken)
            {
                throw new InvalidOperationException(
                    $"Theme {theme} unexpectedly replaced the current render.");
            }

            string screenshot = Path.Combine(
                screenshotDirectory,
                theme.ToString().ToLowerInvariant() + ".png");
            await readerHost.CaptureBaselinePreviewAsync(screenshot);
            screenshots.Add($"screenshots/{Path.GetFileName(screenshot)}");
            states.Add(DecodeScriptResult(await readerHost.ExecuteInFixtureFrameAsync(
                "custom-viewer",
                "JSON.stringify({viewer:document.body.dataset.viewer||'',scrollY,script:" +
                "Boolean(window.fixtureExternal),media:document.querySelectorAll('audio').length," +
                "engine:document.documentElement.dataset.vdictDarkEngine||''})")));
        }

        await QueryAsync(
            "theme-css-state",
            "fixture-css",
            "same-process",
            cancellationToken);
        string cssFrameBefore = DecodeScriptResult(
            await readerHost.ExecuteInFixtureFrameAsync(
                "fixture-css-marker",
                "(()=>{const c=document.querySelector('.fixture-css-collapsible');" +
                "c.dataset.collapsed='true';" +
                "return JSON.stringify({collapsed:c.dataset.collapsed});})()"));
        string cssShellBefore = DecodeScriptResult(
            await readerHost.ExecuteInShellAsync(
                "(()=>{scrollTo(0,Math.min(600,document.documentElement.scrollHeight-innerHeight));" +
                "return JSON.stringify({scrollY,scrollHeight:document.documentElement.scrollHeight});})()"));
        string cssStateBefore = $"frame={cssFrameBefore};shell={cssShellBefore}";
        List<string> cssStates = [];
        foreach (ApplicationTheme theme in new[]
                 {
                     ApplicationTheme.Dark,
                     ApplicationTheme.Light
                 })
        {
            _ = await ApplyAndMeasureThemeAsync(theme, cancellationToken);
            string cssFrameState = DecodeScriptResult(
                await readerHost.ExecuteInFixtureFrameAsync(
                    "fixture-css-marker",
                    "(()=>{const c=document.querySelector('.fixture-css-collapsible');" +
                    "return JSON.stringify({collapsed:c.dataset.collapsed," +
                    "engine:document.documentElement.dataset.vdictDarkEngine||''});})()"));
            string cssShellState = DecodeScriptResult(
                await readerHost.ExecuteInShellAsync(
                    "JSON.stringify({scrollY,scrollHeight:document.documentElement.scrollHeight})"));
            cssStates.Add($"frame={cssFrameState};shell={cssShellState}");
        }

        await QueryAsync(
            "theme-audio-state",
            "fixture-audio",
            "same-process",
            cancellationToken);
        _ = await readerHost.ExecuteInFixtureFrameAsync(
            "script-audio",
            """
            (()=>{const audio=document.querySelector('#audio-element');
              audio.src='assets/local-tone.wav';audio.loop=true;audio.load();
              document.querySelector('#script-audio').addEventListener('click',event=>{
                event.stopImmediatePropagation();void audio.play();
              },{capture:true,once:true});return true;})()
            """);
        await readerHost.DispatchTrustedClickAsync("script-audio");
        await Task.Delay(250, cancellationToken);
        string audioStateBefore = DecodeScriptResult(
            await readerHost.ExecuteInFixtureFrameAsync(
                "script-audio",
                "(()=>{const a=document.querySelector('#audio-element');" +
                "return JSON.stringify({paused:a.paused,loop:a.loop,currentTime:a.currentTime});})()"));
        if (audioStateBefore.Contains("\"paused\":true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Theme audio baseline did not reach the playing state.");
        }

        List<string> audioStates = [];
        foreach (ApplicationTheme theme in new[]
                 {
                     ApplicationTheme.Dark,
                     ApplicationTheme.Light
                 })
        {
            _ = await ApplyAndMeasureThemeAsync(theme, cancellationToken);
            string audioState = DecodeScriptResult(
                await readerHost.ExecuteInFixtureFrameAsync(
                    "script-audio",
                    "(()=>{const a=document.querySelector('#audio-element');" +
                    "return JSON.stringify({paused:a.paused,loop:a.loop,currentTime:a.currentTime," +
                    "engine:document.documentElement.dataset.vdictDarkEngine||''});})()"));
            if (audioState.Contains("\"paused\":true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Theme {theme} interrupted active audio.");
            }

            audioStates.Add(audioState);
        }

        for (int iteration = 0; iteration < warmSamples; iteration++)
        {
            // System is light in the acceptance environment. Force a dark
            // precondition so the Light sample measures a real engine disable.
            _ = await ApplyAndMeasureThemeAsync(
                ApplicationTheme.Dark,
                cancellationToken);
            foreach (ApplicationTheme theme in new[]
                 {
                     ApplicationTheme.Light,
                     ApplicationTheme.Dark,
                     ApplicationTheme.System
                  })
            {
                double themeSettleMilliseconds =
                    await ApplyAndMeasureThemeAsync(theme, cancellationToken);
                BaselineQuerySample sample = await QueryAsync(
                    "theme-" + theme.ToString().ToLowerInvariant(),
                    "fixture-perf-large",
                    "same-process",
                    cancellationToken);
                themeSamples.Add(new BaselineThemeSample(
                    theme.ToString(),
                    iteration + 1,
                    sample.EndToEndMilliseconds,
                    sample.NavigationToFirstFrameReadyMilliseconds,
                    sample.NavigationToAllFramesReadyMilliseconds,
                    themeSettleMilliseconds));
            }
        }

        return new BaselineThemeResult(
            before,
            states,
            cssStateBefore,
            cssStates,
            audioStateBefore,
            audioStates,
            screenshots,
            themeSamples);
    }

    private async Task<double> ApplyAndMeasureThemeAsync(
        ApplicationTheme theme,
        CancellationToken cancellationToken)
    {
        string token = reader.Document?.RenderToken ?? string.Empty;
        Stopwatch timer = Stopwatch.StartNew();
        await CommitThemeAsync(theme, cancellationToken);
        await WaitForDynamicThemeAsync(token, cancellationToken);
        timer.Stop();
        return timer.Elapsed.TotalMilliseconds;
    }

    private async Task WaitForDynamicThemeAsync(
        string renderToken,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < RenderTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Document?.RenderToken != renderToken)
            {
                throw new InvalidOperationException(
                    "Dynamic theme synchronization changed the RenderToken.");
            }

            string state = DecodeScriptResult(
                await readerHost.ExecuteInFirstFrameAsync(
                    "JSON.stringify({engine:document.documentElement.dataset.vdictDarkEngine||''," +
                    "controller:typeof window.vdictApplyTheme==='function'})"));
            if (state.Contains("\"controller\":true", StringComparison.Ordinal) &&
                (state.Contains("\"engine\":\"darkreader\"", StringComparison.Ordinal) ||
                 state.Contains("\"engine\":\"original\"", StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Dynamic dictionary theme did not settle.");
    }

    private async Task CommitThemeAsync(
        ApplicationTheme theme,
        CancellationToken cancellationToken)
    {
        ApplicationSettings current = catalog.GetSettingsSnapshot();
        ApplicationSettings updated = current with
        {
            Appearance = current.Appearance with { Theme = theme }
        };
        SettingsPrepareResult preparation = await catalog.PrepareUpdateAsync(
            updated,
            cancellationToken);
        if (preparation.Plan is null)
        {
            throw new InvalidOperationException(
                $"Theme baseline prepare failed for {theme}: " +
                string.Join(", ", preparation.Errors.Select(error => error.Message)));
        }

        SettingsCommitResult commit = await catalog.CommitUpdateAsync(
            preparation.Plan,
            cancellationToken);
        if (!commit.Succeeded)
        {
            throw new InvalidOperationException(
                $"Theme baseline commit failed for {theme}: {commit.Error}");
        }
    }

    private async Task<ReaderDocument> WaitForNewDocumentAsync(
        string previousToken,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < RenderTimeout)
        {
            if (reader.Document is { } document &&
                !document.RenderToken.Equals(previousToken, StringComparison.Ordinal))
            {
                return document;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException("Settings commit did not produce a new reader document.");
    }

    private async Task<BaselineZoomResult> RunZoomAsync(CancellationToken cancellationToken)
    {
        _ = await QueryAsync(
            "zoom",
            "fixture-basic",
            "same-process",
            cancellationToken);
        DictionaryCatalogSnapshot beforeCatalog = catalog.GetSnapshot();
        string beforeToken = reader.Document?.RenderToken ?? string.Empty;
        long beforeLookup = reader.DiagnosticLookupExecutionCount;
        long beforeNavigation = readerHost.DiagnosticNavigationSubmitCount;
        long beforeScripts = readerHost.DiagnosticScriptExecutionCount;
        long beforeRebind = mainWindow.DiagnosticDictionaryRebindCount;
        double startingZoom = reader.Zoom;
        for (int index = 1; index <= 30; index++)
        {
            reader.SetZoom(Math.Clamp(startingZoom + index * 0.01, 0.5, 2.5));
        }

        await Task.Delay(1200, cancellationToken);
        if (reader.Document is { } changed &&
            !changed.RenderToken.Equals(beforeToken, StringComparison.Ordinal))
        {
            _ = await readerHost.WaitForRenderAsync(
                changed.RenderToken,
                RenderTimeout,
                cancellationToken);
        }

        DictionaryCatalogSnapshot afterCatalog = catalog.GetSnapshot();
        return new BaselineZoomResult(
            30,
            startingZoom,
            reader.Zoom,
            beforeCatalog.SettingsRevision,
            afterCatalog.SettingsRevision,
            beforeCatalog.Generation,
            afterCatalog.Generation,
            beforeToken[..Math.Min(8, beforeToken.Length)],
            reader.Document?.RenderToken[..Math.Min(8, reader.Document.RenderToken.Length)] ?? string.Empty,
            reader.DiagnosticLookupExecutionCount - beforeLookup,
            readerHost.DiagnosticNavigationSubmitCount - beforeNavigation,
            readerHost.DiagnosticScriptExecutionCount - beforeScripts,
            mainWindow.DiagnosticDictionaryRebindCount - beforeRebind);
    }

    private async Task<BaselineSettingsConcurrencyResult> RunSettingsConcurrencyAsync(
        CancellationToken cancellationToken)
    {
        DictionaryCatalogSnapshot unmodifiedBefore = catalog.GetSnapshot();
        double unmodifiedBaseZoom = catalog.GetSettingsSnapshot().Appearance.ContentZoom;
        double unmodifiedPendingZoom = Math.Clamp(unmodifiedBaseZoom + 0.07, 0.5, 2.5);
        reader.SetZoom(unmodifiedPendingZoom);
        SettingsEditSession unmodifiedSession = new(
            catalog.GetSettingsSnapshot(),
            catalog);
        SettingsCommitResult unmodifiedCommit = await unmodifiedSession.SaveAsync(
            cancellationToken);
        await WaitForCatalogRevisionAsync(
            unmodifiedBefore.SettingsRevision + 1,
            cancellationToken);
        DictionaryCatalogSnapshot unmodifiedAfter = catalog.GetSnapshot();
        double unmodifiedFinalZoom = catalog.GetSettingsSnapshot().Appearance.ContentZoom;

        DictionaryCatalogSnapshot explicitBefore = catalog.GetSnapshot();
        double explicitBaseZoom = catalog.GetSettingsSnapshot().Appearance.ContentZoom;
        double explicitPendingZoom = Math.Clamp(explicitBaseZoom + 0.06, 0.5, 2.5);
        double explicitDraftZoom = Math.Clamp(explicitBaseZoom - 0.05, 0.5, 2.5);
        reader.SetZoom(explicitPendingZoom);
        SettingsEditSession explicitSession = new(
            catalog.GetSettingsSnapshot(),
            catalog);
        explicitSession.Update(
            settings => settings with
            {
                Appearance = settings.Appearance with
                {
                    ContentZoom = explicitDraftZoom
                }
            },
            SettingsDirtySection.Reader);
        SettingsCommitResult explicitCommit = await explicitSession.SaveAsync(
            cancellationToken);
        await WaitForCatalogRevisionAsync(
            explicitBefore.SettingsRevision + 1,
            cancellationToken);
        DictionaryCatalogSnapshot explicitAfter = catalog.GetSnapshot();
        double explicitFinalZoom = catalog.GetSettingsSnapshot().Appearance.ContentZoom;

        return new BaselineSettingsConcurrencyResult(
            new BaselineSettingsConcurrencyCase(
                "pending-zoom-unmodified-draft",
                unmodifiedBaseZoom,
                unmodifiedPendingZoom,
                unmodifiedBaseZoom,
                unmodifiedFinalZoom,
                unmodifiedBefore.SettingsRevision,
                unmodifiedAfter.SettingsRevision,
                unmodifiedBefore.Generation,
                unmodifiedAfter.Generation,
                unmodifiedCommit.Succeeded),
            new BaselineSettingsConcurrencyCase(
                "pending-zoom-explicit-draft",
                explicitBaseZoom,
                explicitPendingZoom,
                explicitDraftZoom,
                explicitFinalZoom,
                explicitBefore.SettingsRevision,
                explicitAfter.SettingsRevision,
                explicitBefore.Generation,
                explicitAfter.Generation,
                explicitCommit.Succeeded));
    }

    private async Task WaitForCatalogRevisionAsync(
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < RenderTimeout)
        {
            if (catalog.GetSnapshot().SettingsRevision >= expectedRevision)
            {
                await Task.Delay(800, cancellationToken);
                return;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException(
            $"Catalog revision did not reach {expectedRevision}.");
    }

    private async Task<IReadOnlyList<BaselineCatalogMutation>> RunCatalogMutationMatrixAsync(
        CancellationToken cancellationToken)
    {
        List<BaselineCatalogMutation> results = [];
        ApplicationSettings baseline = catalog.GetSettingsSnapshot();
        DictionaryDefinition original = baseline.Dictionaries[0];

        await ApplyCatalogMutationAsync(
            "disable-dictionary",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with { Enabled = false })
            },
            results,
            cancellationToken);
        await ApplyCatalogMutationAsync(
            "restore-enabled-dictionary",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with { Enabled = true })
            },
            results,
            cancellationToken);

        if (baseline.Dictionaries.Length > 1)
        {
            await ApplyCatalogMutationAsync(
                "change-dictionary-order",
                settings => settings with
                {
                    Dictionaries = settings.Dictionaries
                        .RemoveAt(1)
                        .Insert(0, settings.Dictionaries[1])
                },
                results,
                cancellationToken);
        }
        else
        {
            DictionaryCatalogSnapshot snapshot = catalog.GetSnapshot();
            string capability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                original.Id.Value,
                snapshot.Generation);
            results.Add(new BaselineCatalogMutation(
                "change-dictionary-order",
                snapshot.SettingsRevision,
                snapshot.SettingsRevision,
                snapshot.Generation,
                snapshot.Generation,
                snapshot.ActiveDictionaries.Length,
                snapshot.ActiveDictionaries.Length,
                capability[..8],
                capability[..8],
                false,
                false,
                0,
                0,
                false,
                false,
                "Fixture profile contains one dictionary; execute with a two-dictionary canary profile."));
        }

        await ApplyCatalogMutationAsync(
            "replace-mdd-css-javascript",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with
                    {
                        MddPaths = [],
                        CssPaths = [],
                        ScriptPaths = []
                    })
            },
            results,
            cancellationToken);
        await ApplyCatalogMutationAsync(
            "restore-mdd-css-javascript",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with
                    {
                        MddPaths = original.MddPaths,
                        CssPaths = original.CssPaths,
                        ScriptPaths = original.ScriptPaths
                    })
            },
            results,
            cancellationToken);
        await ApplyCatalogMutationAsync(
            "change-script-and-security-policy",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with
                    {
                        LocalScriptMode = LocalScriptMode.Legacy,
                        BlockNetwork = false
                    }),
                ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
            },
            results,
            cancellationToken);
        await ApplyCatalogMutationAsync(
            "restore-script-and-security-policy",
            settings => settings with
            {
                Dictionaries = settings.Dictionaries.SetItem(
                    0,
                    settings.Dictionaries[0] with
                    {
                        LocalScriptMode = original.LocalScriptMode,
                        BlockNetwork = original.BlockNetwork
                    }),
                ExternalLinkPolicy = baseline.ExternalLinkPolicy
            },
            results,
            cancellationToken);
        await Task.Delay(300, cancellationToken);
        return results;
    }

    private async Task ApplyCatalogMutationAsync(
        string name,
        Func<ApplicationSettings, ApplicationSettings> mutate,
        List<BaselineCatalogMutation> results,
        CancellationToken cancellationToken)
    {
        DictionaryCatalogSnapshot before = catalog.GetSnapshot();
        ApplicationSettings current = catalog.GetSettingsSnapshot();
        DictionaryDefinition? dictionaryBefore = current.Dictionaries.FirstOrDefault();
        string capabilityBefore = dictionaryBefore is null
            ? string.Empty
            : DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                dictionaryBefore.Id.Value,
                before.Generation);
        string renderBefore = reader.Document?.RenderToken ?? string.Empty;
        long lookupBefore = reader.DiagnosticLookupExecutionCount;
        long rebindBefore = mainWindow.DiagnosticDictionaryRebindCount;
        SettingsPrepareResult preparation = await catalog.PrepareUpdateAsync(
            mutate(current),
            cancellationToken);
        if (preparation.Plan is null)
        {
            throw new InvalidOperationException(
                $"Catalog baseline mutation '{name}' failed to prepare: " +
                string.Join(", ", preparation.Errors.Select(error => error.Message)));
        }

        SettingsCommitResult commit = await catalog.CommitUpdateAsync(
            preparation.Plan,
            cancellationToken);
        if (!commit.Succeeded)
        {
            throw new InvalidOperationException(
                $"Catalog baseline mutation '{name}' failed: {commit.Error}");
        }

        await Task.Delay(150, cancellationToken);
        DictionaryCatalogSnapshot after = catalog.GetSnapshot();
        DictionaryDefinition? dictionaryAfter = catalog
            .GetSettingsSnapshot()
            .Dictionaries
            .FirstOrDefault();
        string capabilityAfter = dictionaryAfter is null
            ? string.Empty
            : DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                dictionaryAfter.Id.Value,
                after.Generation);
        results.Add(new BaselineCatalogMutation(
            name,
            before.SettingsRevision,
            after.SettingsRevision,
            before.Generation,
            after.Generation,
            before.ActiveDictionaries.Length,
            after.ActiveDictionaries.Length,
            capabilityBefore[..Math.Min(8, capabilityBefore.Length)],
            capabilityAfter[..Math.Min(8, capabilityAfter.Length)],
            !capabilityBefore.Equals(capabilityAfter, StringComparison.Ordinal),
            before.Generation != after.Generation,
            reader.DiagnosticLookupExecutionCount - lookupBefore,
            mainWindow.DiagnosticDictionaryRebindCount - rebindBefore,
            !renderBefore.Equals(
                reader.Document?.RenderToken ?? string.Empty,
                StringComparison.Ordinal),
            true,
            string.Empty));
    }

    private void ReaderHost_StatusChanged(object? sender, string message)
    {
        if (_statusEvents.Count < 128)
        {
            _statusEvents.Add(message);
        }
    }

    private static string DecodeScriptResult(string result)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(result) ?? result;
        }
        catch (JsonException)
        {
            return result;
        }
    }
}

internal sealed class UiStallMonitor(DispatcherQueue dispatcherQueue)
{
    private readonly DispatcherQueueTimer _timer = dispatcherQueue.CreateTimer();
    private long _lastTimestamp;

    public double MaximumStallMilliseconds { get; private set; }

    public void Start()
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
        _lastTimestamp = now;
        MaximumStallMilliseconds = Math.Max(MaximumStallMilliseconds, Math.Max(0, elapsed - 16));
    }
}

internal sealed record BaselineReport(
    string Schema,
    DateTimeOffset CapturedAtUtc,
    BaselineEnvironment Environment,
    int WarmSamplesPerScenario,
    IReadOnlyList<BaselineQuerySample> QuerySamples,
    IReadOnlyList<BaselineQuerySample> CompatibilityQueries,
    BaselineRapidLookupResult RapidLookup,
    BaselineInteractionResult Interactions,
    BaselineThemeResult Themes,
    BaselineZoomResult Zoom,
    BaselineSettingsConcurrencyResult SettingsConcurrency,
    IReadOnlyList<BaselineCatalogMutation> CatalogMutations,
    BaselineResources Resources,
    IReadOnlyList<string> StatusEvents);

internal sealed record BaselineEnvironment(
    string OperatingSystem,
    string Architecture,
    int LogicalProcessors,
    long AvailableMemoryBytes,
    bool Is64BitProcess,
    string WebView2Runtime,
    string Build,
    string PowerMode,
    string BackgroundLoad,
    string DataRoot,
    string WebView2UserData);

internal sealed record BaselineQuerySample(
    string Scenario,
    string Query,
    string CacheState,
    string Render,
    int Frames,
    double EndToEndMilliseconds,
    double NavigationToFirstFrameReadyMilliseconds,
    double NavigationToAllFramesReadyMilliseconds,
    double EntryRewriteMilliseconds,
    double DocumentAssemblyMilliseconds,
    double DocumentBuildMilliseconds,
    double ProcessCpuMilliseconds,
    long AllocatedBytes,
    long ProcessWorkingSetBytes,
    long WebViewProcessMemoryBytes);

internal sealed record BaselineRapidLookupResult(
    int Bursts,
    int InputsPerBurst,
    int MaximumStartedPerBurst,
    int CancellationSamples,
    double CancellationObservationP50Milliseconds,
    double CancellationObservationP95Milliseconds,
    double CancellationObservationMaximumMilliseconds,
    string FinalWord,
    string FinalRenderToken);

internal sealed record BaselineInteractionResult(
    string ImageBefore,
    string ImageAfter,
    string Audio,
    string Security,
    IReadOnlyList<string> SecurityEvents,
    bool SecurityNavigationStayedOnCurrentRender);

internal sealed record BaselineThemeResult(
    string StateBeforeThemeChanges,
    IReadOnlyList<string> StatesAfterThemeChanges,
    string CssStateBeforeThemeChanges,
    IReadOnlyList<string> CssStatesAfterThemeChanges,
    string AudioStateBeforeThemeChanges,
    IReadOnlyList<string> AudioStatesAfterThemeChanges,
    IReadOnlyList<string> Screenshots,
    IReadOnlyList<BaselineThemeSample> Samples);

internal sealed record BaselineThemeSample(
    string Theme,
    int Iteration,
    double EndToEndMilliseconds,
    double NavigationToFirstFrameReadyMilliseconds,
    double NavigationToAllFramesReadyMilliseconds,
    double DynamicThemeSettleMilliseconds);

internal sealed record BaselineZoomResult(
    int InputCount,
    double StartingZoom,
    double FinalZoom,
    long SettingsRevisionBefore,
    long SettingsRevisionAfter,
    long CatalogGenerationBefore,
    long CatalogGenerationAfter,
    string RenderBefore,
    string RenderAfter,
    long LookupCountDelta,
    long NavigationSubmitDelta,
    long ScriptExecutionDelta,
    long DictionaryRebindDelta);

internal sealed record BaselineSettingsConcurrencyResult(
    BaselineSettingsConcurrencyCase UnmodifiedDraft,
    BaselineSettingsConcurrencyCase ExplicitZoomDraft);

internal sealed record BaselineSettingsConcurrencyCase(
    string Scenario,
    double BaseZoom,
    double PendingZoom,
    double DraftZoom,
    double FinalZoom,
    long SettingsRevisionBefore,
    long SettingsRevisionAfter,
    long CatalogGenerationBefore,
    long CatalogGenerationAfter,
    bool DraftCommitSucceeded);

internal sealed record BaselineCatalogMutation(
    string Change,
    long SettingsRevisionBefore,
    long SettingsRevisionAfter,
    long CatalogGenerationBefore,
    long CatalogGenerationAfter,
    int ActiveDictionariesBefore,
    int ActiveDictionariesAfter,
    string CapabilityBefore,
    string CapabilityAfter,
    bool CapabilityChanged,
    bool OldGrantRevokedByGeneration,
    long LookupCountDelta,
    long DictionaryRebindDelta,
    bool RenderTokenChanged,
    bool Executed,
    string Note);

internal sealed record BaselineResources(
    long ProcessWorkingSetBytes,
    long ProcessPrivateMemoryBytes,
    long TotalAllocatedBytes,
    int BackgroundBuildConcurrencyPeak,
    double MaximumUiThreadStallMilliseconds,
    double RunDurationMilliseconds);
