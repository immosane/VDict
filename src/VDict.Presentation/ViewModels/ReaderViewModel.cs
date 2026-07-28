using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VDict.Core.Catalog;
using VDict.Core.Environment;
using VDict.Core.Localization;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Core.Text;
using VDict.Presentation.Collections;
using VDict.Presentation.Services;

namespace VDict.Presentation.ViewModels;

public enum CatalogLifecycleState
{
    Initializing,
    Ready,
    Failed
}

public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private readonly IDictionaryCatalog _catalog;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeSpan _candidateDebounce;
    private readonly TimeSpan _zoomPersistenceDebounce;
    private readonly IAppLogger? _logger;
    private readonly IAppLocalizer _localizer;
    private readonly bool _backgroundBuildEnabled;
    private readonly LookupHistory _history = new(100);
    private readonly ResettableObservableCollection<string> _candidates = [];
    private readonly LatestWinsWorkQueue<LookupWork> _lookupQueue;
    private CancellationTokenSource? _candidateCts;
    private CancellationTokenSource? _legacyLookupCts;
    private CancellationTokenSource? _zoomCts;
    private Task _zoomPersistenceTask = Task.CompletedTask;
    private long _pendingZoomBaseRevision;
    private double _pendingZoomBaseValue = 1.0;
    private long _candidateVersion;
    private long _lookupRequestId;
    private long _lookupExecutionCount;
    private bool _suppressSearchEdit;
    private bool _suppressCandidateRefresh;
    private bool _manualQueryEdited;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _currentWord = string.Empty;

    [ObservableProperty]
    private DictionaryId? _selectedDictionaryId;

    [ObservableProperty]
    private bool _isLookingUp;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private ReaderDocument? _document;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private ImmutableArray<string> _lookupPath = [];

    [ObservableProperty]
    private CatalogLifecycleState _catalogState = CatalogLifecycleState.Initializing;

    [ObservableProperty]
    private string _candidateStateMessage = string.Empty;

    public bool CanSelectDictionary =>
        CatalogState == CatalogLifecycleState.Ready &&
        !_catalog.GetSnapshot().ActiveDictionaries.IsDefaultOrEmpty;

    public string ZoomPercent => $"{Math.Round(Zoom * 100):0}%";

    public long DiagnosticLookupExecutionCount =>
        Interlocked.Read(ref _lookupExecutionCount);

    public int DiagnosticBackgroundBuildConcurrencyPeak =>
        _lookupQueue.PeakActiveCount;

    public ImmutableArray<double> DiagnosticCancellationObservationMilliseconds =>
        _lookupQueue.CancellationObservationMilliseconds;

    public ImmutableArray<LookupPathItem> LookupPathItems =>
        LookupPathState.CreateItems(
            LookupPath,
            _localizer.Get("Reader_Path_Current"),
            _localizer.Get("Reader_Path_Item"));

    public ReaderViewModel(
        IDictionaryCatalog catalog,
        IUiDispatcher dispatcher,
        TimeSpan? candidateDebounce = null,
        TimeSpan? zoomPersistenceDebounce = null,
        IAppLogger? logger = null,
        bool? backgroundBuildEnabled = null,
        IAppLocalizer? localizer = null)
    {
        _catalog = catalog;
        _dispatcher = dispatcher;
        _candidateDebounce = candidateDebounce ?? TimeSpan.FromMilliseconds(80);
        _zoomPersistenceDebounce =
            zoomPersistenceDebounce ?? TimeSpan.FromMilliseconds(350);
        _logger = logger;
        _localizer = localizer ?? IdentityAppLocalizer.Instance;
        _candidateStateMessage = _localizer.Get("Reader_Candidates_EnterWord");
        _backgroundBuildEnabled = backgroundBuildEnabled ??
            !string.Equals(
                System.Environment.GetEnvironmentVariable(
                    "VDICT_DISABLE_BACKGROUND_RENDER_PIPELINE"),
                "1",
                StringComparison.Ordinal);
        _lookupQueue = new LatestWinsWorkQueue<LookupWork>(ExecuteLookupAsync);
        _document = ReaderDocumentBuilder.Welcome(catalog.GetSettingsSnapshot());
        _zoom = catalog.GetSettingsSnapshot().Appearance.ContentZoom;
        LookupCommand = new AsyncRelayCommand(
            () => LookupAsync(SearchText),
            () => CatalogState == CatalogLifecycleState.Ready &&
                  !string.IsNullOrWhiteSpace(SearchText));
        CancelCommand = new RelayCommand(Cancel, () => IsLookingUp);
        BackCommand = new AsyncRelayCommand(GoBackAsync, () => _history.CanGoBack && !IsLookingUp);
        ForwardCommand = new AsyncRelayCommand(GoForwardAsync, () => _history.CanGoForward && !IsLookingUp);
    }

    public ObservableCollection<string> Candidates => _candidates;

    public IAsyncRelayCommand LookupCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand BackCommand { get; }

    public IAsyncRelayCommand ForwardCommand { get; }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressCandidateRefresh)
        {
            _candidates.ReplaceAll([]);
            CandidateStateMessage = string.IsNullOrWhiteSpace(value)
                ? _localizer.Get("Reader_Candidates_EnterWord")
                : _localizer.Get("Reader_Candidates_Searching");
        }

        if (!_suppressSearchEdit &&
            !value.Equals(CurrentWord, StringComparison.Ordinal))
        {
            _manualQueryEdited = true;
            LookupPath = [];
        }

        LookupCommand.NotifyCanExecuteChanged();
        if (!_suppressCandidateRefresh)
        {
            ScheduleCandidates(value);
        }
    }

    partial void OnIsLookingUpChanged(bool value)
    {
        LookupCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectDictionary));
    }

    partial void OnCatalogStateChanged(CatalogLifecycleState value)
    {
        LookupCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectDictionary));
    }

    partial void OnZoomChanged(double value) =>
        OnPropertyChanged(nameof(ZoomPercent));

    partial void OnLookupPathChanged(ImmutableArray<string> value) =>
        OnPropertyChanged(nameof(LookupPathItems));

    partial void OnSelectedDictionaryIdChanged(DictionaryId? value)
    {
        ScheduleCandidates(SearchText);
    }

    public async Task<bool> LookupAsync(
        string text,
        bool recordHistory = true,
        CancellationToken cancellationToken = default,
        LookupOrigin origin = LookupOrigin.Manual)
    {
        text = text.Trim();
        if (text.Length == 0 || CatalogState != CatalogLifecycleState.Ready)
        {
            return false;
        }

        RefreshDictionarySelection();
        if (SelectedDictionaryId is null)
        {
            return false;
        }

        int hash = text.IndexOf('#');
        string lookupWord = (hash < 0 ? text : text[..hash]).Trim();
        string fragment = hash < 0 ? string.Empty : text[(hash + 1)..].Trim();
        if (lookupWord.Length == 0)
        {
            return false;
        }

        long requestId = Interlocked.Increment(ref _lookupRequestId);
        long startedTimestamp = Stopwatch.GetTimestamp();
        DebugLookupProbe.Hit(
            _logger,
            "LookupStart",
            requestId,
            detail: $"Origin={origin}; QueryLength={lookupWord.Length}",
            source: origin.ToString());
        IsLookingUp = true;
        StatusMessage = _localizer.Get("Reader_Lookup_Searching");
        DictionaryCatalogSnapshot snapshot = _catalog.GetSnapshot();
        ApplicationSettings settings = _catalog.GetSettingsSnapshot();
        double selectionFeedbackMilliseconds =
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        DebugLookupProbe.Record(
            "LookupFeedbackVisible",
            requestId,
            detail: $"ElapsedMs={selectionFeedbackMilliseconds:F3}");
        LookupWork work = new(
            requestId,
            text,
            lookupWord,
            fragment,
            SelectedDictionaryId.Value,
            snapshot.Generation,
            settings,
            Guid.NewGuid().ToString("N"),
            recordHistory,
            origin,
            startedTimestamp,
            selectionFeedbackMilliseconds);
        Stopwatch lookupTimer = Stopwatch.StartNew();
        try
        {
            if (_backgroundBuildEnabled)
            {
                DebugLookupProbe.Record(
                    "LookupQueued",
                    work.RequestId,
                    work.RenderToken,
                    "Mode=LatestWins");
                await _lookupQueue.EnqueueAsync(work, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                DebugLookupProbe.Record(
                    "LookupQueued",
                    work.RequestId,
                    work.RenderToken,
                    "Mode=Legacy");
                await ExecuteLegacyLookupAsync(work, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            DebugLookupProbe.Record(
                "LookupCancelled",
                work.RequestId,
                work.RenderToken);
        }
        catch (Exception exception)
        {
            DebugLookupProbe.Record(
                "LookupFailed",
                work.RequestId,
                work.RenderToken,
                $"Exception={exception.GetType().Name}");
            if (IsCurrentLookup(work))
            {
                await _dispatcher.EnqueueAsync(
                    () => StatusMessage = _localizer.Get("Reader_Lookup_Failed"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            lookupTimer.Stop();
            if (requestId == Interlocked.Read(ref _lookupRequestId))
            {
                await _dispatcher.EnqueueAsync(() => IsLookingUp = false, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            DebugLookupProbe.Record(
                "LookupFinished",
                work.RequestId,
                work.RenderToken,
                $"ElapsedMs={lookupTimer.Elapsed.TotalMilliseconds:F3}");
        }

        return work.Committed;
    }

    private async Task ExecuteLookupAsync(
        LookupWork work,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookupExecutionCount);
        Stopwatch totalTimer = Stopwatch.StartNew();
        Stopwatch stageTimer = Stopwatch.StartNew();
        TimeSpan processCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        long allocatedStart = GC.GetTotalAllocatedBytes(precise: false);
        ReaderDocumentBuildResult? build = null;
        int rendererThread = 0;
        int resultCount = 0;
        try
        {
            DebugLookupProbe.Record(
                "LookupDequeued",
                work.RequestId,
                work.RenderToken,
                $"QueueWaitMs={Stopwatch.GetElapsedTime(work.StartedTimestamp).TotalMilliseconds:F3}");
            LookupBatchResult batch = await _catalog.LookupBatchAsync(
                new LookupQuery(work.LookupWord, work.DictionaryId, work.RequestId),
                cancellationToken).ConfigureAwait(false);
            long lookupMs = stageTimer.ElapsedMilliseconds;
            DebugLookupProbe.Hit(
                _logger,
                "LookupDataReady",
                work.RequestId,
                work.RenderToken,
                $"ElapsedMs={lookupMs}; Results={batch.Results.Length}");
            stageTimer.Restart();
            resultCount = batch.Results.Length;
            ThrowIfLookupStale(work, cancellationToken);

            ReaderDocumentDescription description = ReaderDocumentBuilder.Describe(
                work.LookupWord,
                batch.Results,
                work.Settings,
                batch.Notices,
                work.Fragment,
                work.CatalogGeneration,
                work.RenderToken,
                new ReaderDocumentText(
                    _localizer.Get("Reader_NotFound_Title"),
                    _localizer.Get("Reader_NotFound_Prefix"),
                    _localizer.Get("Reader_NotFound_Suffix")));
            long describeMs = stageTimer.ElapsedMilliseconds;
            DebugLookupProbe.Record(
                "DescriptionBuilt",
                work.RequestId,
                work.RenderToken,
                $"ElapsedMs={describeMs}; Frames={description.Frames.Length}");
            stageTimer.Restart();
            cancellationToken.ThrowIfCancellationRequested();
            rendererThread = System.Environment.CurrentManagedThreadId;
            build = ReaderDocumentBuilder.Build(description, cancellationToken);
            long buildMs = stageTimer.ElapsedMilliseconds;
            DebugLookupProbe.Hit(
                _logger,
                "DocumentBuilt",
                work.RequestId,
                work.RenderToken,
                $"ElapsedMs={buildMs}; Frames={build.Document.Frames.Length}");
            stageTimer.Restart();
            ThrowIfLookupStale(work, cancellationToken);

            DebugLookupProbe.Record(
                "UiDispatchQueued",
                work.RequestId,
                work.RenderToken);
            await _dispatcher.EnqueueAsync(() =>
            {
                long enqueueMs = stageTimer.ElapsedMilliseconds;
                DebugLookupProbe.Record(
                    "UiDispatchStarted",
                    work.RequestId,
                    work.RenderToken,
                    $"QueueWaitMs={enqueueMs}");
                _logger?.Write(VDict.Core.Environment.AppLogLevel.Information, "perf-lookup", $"[perf] Lookup={lookupMs}ms, Describe={describeMs}ms, Build={buildMs}ms, Enqueue={enqueueMs}ms, Frames={build.Document.Frames.Length}");
                if (!IsCurrentLookup(work))
                {
                    DebugLookupProbe.Record(
                        "UiCommitSkipped",
                        work.RequestId,
                        work.RenderToken,
                        "Reason=Stale");
                    return;
                }

                if (work.RecordHistory && CurrentWord.Length > 0)
                {
                    _history.Record(CurrentWord, LookupPath, work.Text);
                }

                CurrentWord = work.Text;
                bool preserveCandidates = work.Origin == LookupOrigin.Candidate;
                if (preserveCandidates)
                {
                    CancelCandidateRefresh();
                }

                _suppressSearchEdit = true;
                _suppressCandidateRefresh = preserveCandidates;
                try
                {
                    SearchText = work.Text;
                }
                finally
                {
                    _suppressCandidateRefresh = false;
                    _suppressSearchEdit = false;
                }

                if (work.RecordHistory)
                {
                    LookupPath = LookupPathState.Update(
                        LookupPath,
                        work.Text,
                        work.Origin,
                        _manualQueryEdited);
                }

                _manualQueryEdited = false;
                Document = build.Document;
                work.Committed = true;
                DebugLookupProbe.Hit(
                    _logger,
                    "DocumentCommitted",
                    work.RequestId,
                    work.RenderToken,
                    $"TotalMs={Stopwatch.GetElapsedTime(work.StartedTimestamp).TotalMilliseconds:F3}");
                StatusMessage = batch.Results.IsDefaultOrEmpty
                    ? _localizer.Format("Reader_Lookup_NotFound", work.Text)
                    : batch.Results.Length == 1
                        ? _localizer.Get("Reader_Lookup_FoundOne")
                        : _localizer.Format(
                            "Reader_Lookup_FoundMany",
                            batch.Results.Length);
                BackCommand.NotifyCanExecuteChanged();
                ForwardCommand.NotifyCanExecuteChanged();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_logger is not null &&
                work.Settings.CompatibilityDiagnosticsEnabled)
            {
                TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - processCpuStart;
                long allocated = Math.Max(
                    0,
                    GC.GetTotalAllocatedBytes(precise: false) - allocatedStart);
                ReaderBuildDiagnostics? diagnostics = build?.Diagnostics;
                string render = build is null
                    ? "none"
                    : build.Document.RenderToken[
                        ..Math.Min(build.Document.RenderToken.Length, 8)];
                double totalMilliseconds =
                    Stopwatch.GetElapsedTime(work.StartedTimestamp).TotalMilliseconds;
                string message = FormattableString.Invariant(
                    $"""Schema=reader-timeline-v1; Stage=LookupSummary; RequestId={work.RequestId}; Render={render}; Origin={work.Origin}; Generation={work.CatalogGeneration}; Results={resultCount}; Entries={diagnostics?.EntryCount ?? 0}; SelectionFeedbackMs={work.SelectionFeedbackMilliseconds:F3}; QueryCompleteMs={totalTimer.Elapsed.TotalMilliseconds:F3}; EntryRewriteMs={diagnostics?.EntryRewriteMilliseconds ?? 0:F3}; DocumentAssemblyMs={diagnostics?.DocumentAssemblyMilliseconds ?? 0:F3}; DocumentBuildMs={diagnostics?.TotalMilliseconds ?? 0:F3}; TotalMs={totalMilliseconds:F3}; ProcessCpuMs={cpu.TotalMilliseconds:F3}; AllocatedBytes={allocated}; RendererThread={rendererThread}; Cancelled={cancellationToken.IsCancellationRequested}; BackgroundConcurrency={_lookupQueue.ActiveCount}""");
                _logger.Write(
                    AppLogLevel.Information,
                    "reader-timeline",
                    message);
            }
        }
    }

    private async Task ExecuteLegacyLookupAsync(
        LookupWork work,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _legacyLookupCts, linked);
        previous?.Cancel();
        try
        {
            await ExecuteLookupAsync(work, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _legacyLookupCts, null, linked);
            linked.Dispose();
        }
    }

    private bool IsCurrentLookup(LookupWork work) =>
        work.RequestId == Interlocked.Read(ref _lookupRequestId) &&
        work.CatalogGeneration == _catalog.GetSnapshot().Generation;

    private void ThrowIfLookupStale(
        LookupWork work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentLookup(work))
        {
            throw new OperationCanceledException(
                "The lookup result is stale.",
                cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true));
        }
    }

    public void SetZoom(double value)
    {
        double normalized = Math.Round(Math.Clamp(value, 0.5, 2.5), 2);
        if (Math.Abs(Zoom - normalized) < 0.0001)
        {
            return;
        }

        Zoom = normalized;
        ScheduleZoomPersistence(normalized);
    }

    public void ApplyCommittedZoom(double value)
    {
        double normalized = Math.Round(Math.Clamp(value, 0.5, 2.5), 2);
        if (Math.Abs(Zoom - normalized) >= 0.0001)
        {
            Zoom = normalized;
        }
    }

    public async Task FlushZoomAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? pending = Interlocked.Exchange(ref _zoomCts, null);
        pending?.Cancel();
        Task previous = _zoomPersistenceTask;
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            pending?.Dispose();
        }

        ApplicationSettings settings = _catalog.GetSettingsSnapshot();
        if (Math.Abs(settings.Appearance.ContentZoom - Zoom) < 0.0001)
        {
            return;
        }

        SettingsCommitResult commit = await _catalog.UpdateZoomAsync(
            Zoom,
            _pendingZoomBaseRevision,
            _pendingZoomBaseValue,
            cancellationToken).ConfigureAwait(false);
        if (!commit.Succeeded)
        {
            throw new InvalidOperationException(
                commit.Error ?? _localizer.Get("Reader_Zoom_SaveFailed"));
        }
    }

    public async Task NavigateLookupPathAsync(int index)
    {
        if (index < 0 || index >= LookupPath.Length)
        {
            return;
        }

        if (index == LookupPath.Length - 1)
        {
            return;
        }

        ImmutableArray<string> previousPath = LookupPath;
        string previousWord = CurrentWord;
        string destination = previousPath[index];
        bool committed = await LookupAsync(
            destination,
            recordHistory: false,
            origin: LookupOrigin.Path).ConfigureAwait(false);
        if (!committed)
        {
            return;
        }

        await _dispatcher.EnqueueAsync(
            () =>
            {
                _history.RecordSnapshot(previousWord, previousPath);
                LookupPath = previousPath.Take(index + 1).ToImmutableArray();
                BackCommand.NotifyCanExecuteChanged();
                ForwardCommand.NotifyCanExecuteChanged();
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    public Task NavigateEntryAsync(string word)
        => NavigateAppendedAsync(word, LookupOrigin.Entry);

    public Task NavigateSelectedTextAsync(string word)
        => NavigateAppendedAsync(word, LookupOrigin.SelectedText);

    private Task NavigateAppendedAsync(string word, LookupOrigin origin)
    {
        _manualQueryEdited = false;
        _suppressSearchEdit = true;
        SearchText = word.Trim();
        _suppressSearchEdit = false;
        return LookupAsync(SearchText, origin: origin);
    }

    public Task LookupCandidateAsync(string word)
    {
        _manualQueryEdited = false;
        DebugLookupProbe.Hit(
            _logger,
            "CandidateInvoked",
            detail: $"QueryLength={word.Trim().Length}");
        return LookupAsync(word, origin: LookupOrigin.Candidate);
    }

    public void MarkCatalogReady()
    {
        CatalogState = CatalogLifecycleState.Ready;
        StatusMessage ??= _localizer.Get("Reader_Catalog_Ready");
        RefreshDictionarySelection();
        ScheduleCandidates(SearchText);
    }

    public void MarkCatalogFailed(string message)
    {
        CatalogState = CatalogLifecycleState.Failed;
        StatusMessage = message;
    }

    public void RefreshWelcome() =>
        Document = _catalog.GetSnapshot().ActiveDictionaries.IsDefaultOrEmpty
            ? ReaderDocumentBuilder.NoDictionaries(_catalog.GetSettingsSnapshot())
            : ReaderDocumentBuilder.Welcome(_catalog.GetSettingsSnapshot());

    public void RefreshDictionarySelection()
    {
        ImmutableArray<DictionaryDefinition> active =
            _catalog.GetSnapshot().ActiveDictionaries;
        DictionaryId? current = SelectedDictionaryId;
        DictionaryId? selected = active.Any(dictionary => dictionary.Id == current)
            ? current
            : active.IsDefaultOrEmpty
                ? null
                : active[0].Id;
        if (SelectedDictionaryId != selected)
        {
            SelectedDictionaryId = selected;
        }

        OnPropertyChanged(nameof(CanSelectDictionary));
        if (selected is null)
        {
            _candidates.ReplaceAll([]);
            CandidateStateMessage = _localizer.Get("Reader_Dictionaries_NoneEnabled");
            StatusMessage = _localizer.Get("Reader_Dictionaries_EnableInSettings");
            Document = ReaderDocumentBuilder.NoDictionaries(
                _catalog.GetSettingsSnapshot());
        }
    }

    public void CancelActiveOperations()
    {
        CancellationTokenSource? candidates =
            Interlocked.Exchange(ref _candidateCts, null);
        candidates?.Cancel();
        Interlocked.Increment(ref _lookupRequestId);
        _lookupQueue.CancelCurrent();
        CancellationTokenSource? legacy =
            Interlocked.Exchange(ref _legacyLookupCts, null);
        legacy?.Cancel();
        _candidates.ReplaceAll([]);
        IsLookingUp = false;
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActiveOperations();
        _ = _lookupQueue.DisposeAsync();
        CancellationTokenSource? zoom = Interlocked.Exchange(ref _zoomCts, null);
        zoom?.Cancel();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            FlushZoomAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Closing must remain bounded; the last persisted value stays valid.
        }
        finally
        {
            zoom?.Dispose();
        }
    }

    private void ScheduleCandidates(string text)
    {
        long version = Interlocked.Increment(ref _candidateVersion);
        CancellationTokenSource current = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _candidateCts, current);
        previous?.Cancel();
        if (CatalogState != CatalogLifecycleState.Ready)
        {
            current.Dispose();
            Interlocked.CompareExchange(ref _candidateCts, null, current);
            return;
        }

        if (SelectedDictionaryId is null)
        {
            _candidates.ReplaceAll([]);
            CandidateStateMessage = _localizer.Get("Reader_Dictionaries_NoneEnabled");
            current.Dispose();
            Interlocked.CompareExchange(ref _candidateCts, null, current);
            return;
        }

        string requestedText = text;
        DictionaryId? requestedDictionary = SelectedDictionaryId;
        long generation = _catalog.GetSnapshot().Generation;
        _ = RefreshCandidatesAsync(
            requestedText,
            requestedDictionary,
            generation,
            version,
            current);
    }

    private void CancelCandidateRefresh()
    {
        Interlocked.Increment(ref _candidateVersion);
        CancellationTokenSource? source =
            Interlocked.Exchange(ref _candidateCts, null);
        source?.Cancel();
    }

    private async Task RefreshCandidatesAsync(
        string requestedText,
        DictionaryId? requestedDictionary,
        long generation,
        long version,
        CancellationTokenSource source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(requestedText))
            {
                await _dispatcher.EnqueueAsync(() =>
                {
                    _candidates.ReplaceAll([]);
                    CandidateStateMessage = _localizer.Get("Reader_Candidates_EnterWord");
                }, source.Token).ConfigureAwait(false);
                return;
            }

            await Task.Delay(_candidateDebounce, source.Token).ConfigureAwait(false);
            SuggestionResult result = await _catalog.SuggestAsync(
                new LookupQuery(requestedText, requestedDictionary, version),
                50,
                source.Token).ConfigureAwait(false);
            if (!CandidateRequestGuard.IsCurrent(
                    version,
                    Interlocked.Read(ref _candidateVersion),
                    requestedText,
                    SearchText,
                    requestedDictionary,
                    SelectedDictionaryId,
                    result.CatalogGeneration,
                    _catalog.GetSnapshot().Generation) ||
                generation != result.CatalogGeneration)
            {
                return;
            }

            await _dispatcher.EnqueueAsync(() =>
            {
                if (!IsCurrentCandidateRequest(
                        requestedText,
                        requestedDictionary,
                        generation,
                        version))
                {
                    return;
                }

                _candidates.ReplaceAll(result.Words);

                CandidateStateMessage = result.Words.IsDefaultOrEmpty
                    ? _localizer.Get("Reader_Candidates_NoMatch")
                    : string.Empty;
            }, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!IsCurrentCandidateRequest(
                    requestedText,
                    requestedDictionary,
                    generation,
                    version))
            {
                return;
            }

            await _dispatcher.EnqueueAsync(
                () =>
                {
                    if (!IsCurrentCandidateRequest(
                            requestedText,
                            requestedDictionary,
                            generation,
                            version))
                    {
                        return;
                    }

                    CandidateStateMessage = _localizer.Get("Reader_Candidates_FailedHint");
                    StatusMessage = _localizer.Get("Reader_Candidates_Failed");
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _candidateCts, null, source);
            source.Dispose();
        }
    }

    private bool IsCurrentCandidateRequest(
        string requestedText,
        DictionaryId? requestedDictionary,
        long generation,
        long version) =>
        CandidateRequestGuard.IsCurrent(
            version,
            Interlocked.Read(ref _candidateVersion),
            requestedText,
            SearchText,
            requestedDictionary,
            SelectedDictionaryId,
            generation,
            _catalog.GetSnapshot().Generation);

    private async Task GoBackAsync()
    {
        if (_history.TryPeekBack(out LookupHistoryEntry destination))
        {
#if DEBUG
            using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                "HistoryBack",
                $"QueryLength={destination.Word.Length}");
#endif
            string previousWord = CurrentWord;
            ImmutableArray<string> previousPath = LookupPath;
            bool committed = await LookupAsync(
                destination.Word,
                recordHistory: false,
                origin: LookupOrigin.History).ConfigureAwait(false);
            if (!committed)
            {
                return;
            }

            await _dispatcher.EnqueueAsync(
                () =>
                {
                    if (_history.CommitBack(previousWord, previousPath, destination))
                    {
                        LookupPath = destination.Path;
                        BackCommand.NotifyCanExecuteChanged();
                        ForwardCommand.NotifyCanExecuteChanged();
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task GoForwardAsync()
    {
        if (_history.TryPeekForward(out LookupHistoryEntry destination))
        {
#if DEBUG
            using IDisposable interaction = DebugLookupProbe.BeginInteraction(
                "HistoryForward",
                $"QueryLength={destination.Word.Length}");
#endif
            string previousWord = CurrentWord;
            ImmutableArray<string> previousPath = LookupPath;
            bool committed = await LookupAsync(
                destination.Word,
                recordHistory: false,
                origin: LookupOrigin.History).ConfigureAwait(false);
            if (!committed)
            {
                return;
            }

            await _dispatcher.EnqueueAsync(
                () =>
                {
                    if (_history.CommitForward(previousWord, previousPath, destination))
                    {
                        LookupPath = destination.Path;
                        BackCommand.NotifyCanExecuteChanged();
                        ForwardCommand.NotifyCanExecuteChanged();
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void Cancel()
    {
        DebugLookupProbe.Record(
            "CancelRequested",
            Interlocked.Read(ref _lookupRequestId));
        Interlocked.Increment(ref _lookupRequestId);
        _lookupQueue.CancelCurrent();
        IsLookingUp = false;
    }

    private void ScheduleZoomPersistence(double zoom)
    {
        ApplicationSettings baseSettings = _catalog.GetSettingsSnapshot();
        _pendingZoomBaseRevision = baseSettings.Revision;
        _pendingZoomBaseValue = baseSettings.Appearance.ContentZoom;
        CancellationTokenSource current = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _zoomCts, current);
        previous?.Cancel();
        _zoomPersistenceTask = PersistZoomAsync(
            zoom,
            _pendingZoomBaseRevision,
            _pendingZoomBaseValue,
            current);
    }

    private async Task PersistZoomAsync(
        double zoom,
        long baseRevision,
        double baseZoom,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_zoomPersistenceDebounce, source.Token).ConfigureAwait(false);
            SettingsCommitResult commit = await _catalog.UpdateZoomAsync(
                zoom,
                baseRevision,
                baseZoom,
                source.Token).ConfigureAwait(false);
            if (!commit.Succeeded)
            {
                throw new InvalidOperationException(
                    commit.Error ?? _localizer.Get("Reader_Zoom_SaveFailed"));
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await _dispatcher.EnqueueAsync(
                () => StatusMessage = _localizer.Get("Reader_Zoom_SaveFailed"),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _zoomCts, null, source);
            source.Dispose();
        }
    }

    private sealed record LookupWork(
        long RequestId,
        string Text,
        string LookupWord,
        string Fragment,
        DictionaryId DictionaryId,
        long CatalogGeneration,
        ApplicationSettings Settings,
        string RenderToken,
        bool RecordHistory,
        LookupOrigin Origin,
        long StartedTimestamp,
        double SelectionFeedbackMilliseconds)
    {
        public bool Committed { get; set; }
    }

}
