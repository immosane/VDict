using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VDict.Core.Localization;
using VDict.Presentation.Services;
using VDict.WinUI.Platform;
using Windows.Graphics;
using WinRT.Interop;

#if DEBUG
using VDict.Core.Environment;
#endif

namespace VDict.WinUI.Diagnostics;

public sealed partial class DebugTimelineWindow : Window
{
    private readonly IClipboardService _clipboard;
    private readonly IAppLocalizer _localizer;
    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
#if DEBUG
    private AppWindow? _appWindow;
    private long _lastSequence;
#endif
    private bool _paused;

    public DebugTimelineWindow(
        IClipboardService clipboard,
        IAppLocalizer localizer)
    {
        _clipboard = clipboard;
        _localizer = localizer;
        InitializeComponent();
        Title = _localizer.Get("DebugTimelineWindow.Title");
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += DebugTimelineWindow_Closed;
#if DEBUG
        ConfigureWindow();
        RefreshRows();
        _refreshTimer.Start();
#endif
    }

    public ObservableCollection<DebugTimelineRow> Rows { get; } = [];

    internal event EventHandler? WindowClosed;

#if DEBUG
    internal void PositionBeside(AppWindow owner)
    {
        if (_appWindow is null)
        {
            return;
        }

        DisplayArea display = DisplayArea.GetFromWindowId(
            owner.Id,
            DisplayAreaFallback.Nearest);
        RectInt32 workArea = display.WorkArea;
        SizeInt32 size = _appWindow.Size;
        int gap = 12;
        int right = owner.Position.X + owner.Size.Width + gap;
        int left = owner.Position.X - size.Width - gap;
        int x = right + size.Width <= workArea.X + workArea.Width
            ? right
            : left >= workArea.X
                ? left
                : Math.Max(workArea.X, workArea.X + workArea.Width - size.Width);
        int y = Math.Clamp(
            owner.Position.Y,
            workArea.Y,
            Math.Max(workArea.Y, workArea.Y + workArea.Height - size.Height));
        _appWindow.Move(new PointInt32(x, y));
    }

    private void ConfigureWindow()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        WindowSizing.ResizeToWorkArea(_appWindow, hwnd, 720, 460, 560, 320);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }
    }
#endif

    private void RefreshTimer_Tick(object? sender, object e)
    {
#if DEBUG
        if (!_paused)
        {
            RefreshRows();
        }
#endif
    }

#if DEBUG
    private void RefreshRows()
    {
        ImmutableArray<DebugTimelineEvent> pending =
            DebugLookupProbe.SnapshotAfter(_lastSequence);
        foreach (DebugTimelineEvent item in pending)
        {
            Rows.Add(DebugTimelineRow.From(item));
            _lastSequence = Math.Max(_lastSequence, item.Sequence);
        }

        while (Rows.Count > 500)
        {
            Rows.RemoveAt(0);
        }

        if (pending.Length > 0 && Rows.Count > 0)
        {
            TimelineList.ScrollIntoView(Rows[^1]);
        }
    }
#endif

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _localizer.Get(
            _paused ? "DebugTimeline_Resume.Content" : "DebugTimeline_Pause.Content");
        StateText.Text = _localizer.Get(
            _paused ? "DebugTimeline_StatePaused.Text" : "DebugTimeline_State.Text");
        LiveIndicator.Fill = _paused
            ? new SolidColorBrush(Colors.Orange)
            : Application.Current.Resources["SystemFillColorSuccessBrush"] as Brush;
#if DEBUG
        if (!_paused)
        {
            RefreshRows();
        }
#endif
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        DebugLookupProbe.ClearTimeline();
#endif
        Rows.Clear();
#if DEBUG
        _lastSequence = 0;
#endif
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new();
        text.AppendLine("Time\tElapsedMs\tIntervalMs\tCorrelation\tStage\tDetail");
        foreach (DebugTimelineRow row in Rows)
        {
            text.Append(row.TimeText).Append('\t')
                .Append(row.ElapsedText).Append('\t')
                .Append(row.IntervalText).Append('\t')
                .Append(row.CorrelationText).Append('\t')
                .Append(EscapeTsv(row.Stage)).Append('\t')
                .AppendLine(EscapeTsv(row.Detail));
        }

        await _clipboard.SetTextAsync(text.ToString()).ConfigureAwait(true);
    }

    private void DebugTimelineWindow_Closed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        Closed -= DebugTimelineWindow_Closed;
        WindowClosed?.Invoke(this, EventArgs.Empty);
    }

    private static string EscapeTsv(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}

public sealed class DebugTimelineRow
{
    public DebugTimelineRow(
        string timeText,
        string elapsedText,
        string intervalText,
        string correlationText,
        string stage,
        string detail)
    {
        TimeText = timeText;
        ElapsedText = elapsedText;
        IntervalText = intervalText;
        CorrelationText = correlationText;
        Stage = stage;
        Detail = detail;
    }

    public string TimeText { get; set; }

    public string ElapsedText { get; set; }

    public string IntervalText { get; set; }

    public string CorrelationText { get; set; }

    public string Stage { get; set; }

    public string Detail { get; set; }

#if DEBUG
    internal static DebugTimelineRow From(DebugTimelineEvent item)
    {
        string correlation = item.RequestId > 0
            ? $"R{item.RequestId}/{Render(item.RenderToken)}"
            : item.RenderToken.Length > 0
                ? Render(item.RenderToken)
                : $"I{item.InteractionId}";
        string detail = string.IsNullOrEmpty(item.Detail)
            ? $"Source={item.TriggerSource}; Thread={item.ThreadId}"
            : $"{item.Detail}; Source={item.TriggerSource}; Thread={item.ThreadId}";
        return new DebugTimelineRow(
            item.LocalTime.ToString("HH:mm:ss.fff"),
            item.ElapsedMilliseconds.ToString("F3"),
            item.IntervalMilliseconds.ToString("F3"),
            correlation,
            item.Stage,
            detail);
    }

    private static string Render(string renderToken) =>
        renderToken.Length == 0 ? "no-render" : $"W{renderToken}";
#endif
}
