param(
    [Parameter(Mandatory)]
    [int] $ProcessId,

    [switch] $PromoteWebView
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public struct ProbePoint
{
    public int X;
    public int Y;
    public ProbePoint(int x, int y) { X = x; Y = y; }
}

public struct ProbeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class NativeInputProbe
{
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(ProbePoint point);

    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(
        IntPtr parent, ProbePoint point, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out ProbeRect rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr window, out ProbeRect rect);

    [DllImport("user32.dll")]
    public static extern int MapWindowPoints(
        IntPtr from, IntPtr to, ref ProbePoint point, uint count);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr window, bool altTab);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out ProbePoint point);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("user32.dll")]
    public static extern void keybd_event(
        byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent, EnumWindowsProc callback, IntPtr state);

    public static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    public static IntPtr[] Descendants(IntPtr parent)
    {
        var result = new List<IntPtr>();
        EnumChildWindows(parent, (window, state) =>
        {
            result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@

function Format-Hwnd([IntPtr] $window) {
    return ('0x{0:X}' -f $window.ToInt64())
}

function Get-NativeWindow([IntPtr] $window) {
    $rect = [ProbeRect]::new()
    $hasRect = [NativeInputProbe]::GetWindowRect($window, [ref]$rect)
    [pscustomobject]@{
        Hwnd = Format-Hwnd $window
        ClassName = [NativeInputProbe]::ClassName($window)
        Parent = Format-Hwnd ([NativeInputProbe]::GetParent($window))
        PreviousZ = Format-Hwnd ([NativeInputProbe]::GetWindow($window, 3))
        NextZ = Format-Hwnd ([NativeInputProbe]::GetWindow($window, 2))
        Visible = [NativeInputProbe]::IsWindowVisible($window)
        Enabled = [NativeInputProbe]::IsWindowEnabled($window)
        Rect = if ($hasRect) {
            "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
        } else { $null }
    }
}

$processCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $ProcessId)
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $processCondition)
if ($null -eq $root) {
    throw "VDict UI Automation root was not found for process $ProcessId."
}

$readerCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    'ReaderSurface')
$reader = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    $readerCondition)
if ($null -eq $reader) {
    throw 'ReaderSurface was not found.'
}

$main = [IntPtr]$root.Current.NativeWindowHandle
$activationThread = [NativeInputProbe]::GetCurrentThreadId()
$mainThread = [NativeInputProbe]::GetWindowThreadProcessId($main, [IntPtr]::Zero)
$activationAttached = $activationThread -ne $mainThread -and
    [NativeInputProbe]::AttachThreadInput($activationThread, $mainThread, $true)
try {
    [NativeInputProbe]::ShowWindow($main, 9) | Out-Null
    [NativeInputProbe]::BringWindowToTop($main) | Out-Null
    [NativeInputProbe]::SwitchToThisWindow($main, $true)
    [NativeInputProbe]::SetForegroundWindow($main) | Out-Null
    [NativeInputProbe]::SetWindowPos(
        $main, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
}
finally {
    if ($activationAttached) {
        [NativeInputProbe]::AttachThreadInput($activationThread, $mainThread, $false) | Out-Null
    }
}
Start-Sleep -Milliseconds 200
$directWebView = [NativeInputProbe]::Descendants($main) |
    Where-Object {
        [NativeInputProbe]::GetParent($_) -eq $main -and
        [NativeInputProbe]::ClassName($_) -like 'Chrome_WidgetWin_*'
    } |
    Select-Object -First 1
if ($PromoteWebView -and $null -ne $directWebView) {
    [NativeInputProbe]::SetWindowPos(
        $directWebView, [IntPtr]::Zero, 0, 0, 0, 0,
        0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
}
$bounds = $reader.Current.BoundingRectangle
$readerLeft = [double]$bounds.Left
$readerTop = [double]$bounds.Top
$readerWidth = [double]$bounds.Width
$readerHeight = [double]$bounds.Height
$inset = [Math]::Max(4.0, [Math]::Min($readerWidth, $readerHeight) * 0.02)
$points = [ordered]@{
    Center = @(($readerLeft + $readerWidth / 2), ($readerTop + $readerHeight / 2))
    TopLeft = @(($readerLeft + $inset), ($readerTop + $inset))
    TopRight = @(($readerLeft + $readerWidth - $inset), ($readerTop + $inset))
    BottomLeft = @(($readerLeft + $inset), ($readerTop + $readerHeight - $inset))
    BottomRight = @(($readerLeft + $readerWidth - $inset), ($readerTop + $readerHeight - $inset))
}

$hits = foreach ($entry in $points.GetEnumerator()) {
    $screenPoint = [ProbePoint]::new([int]$entry.Value[0], [int]$entry.Value[1])
    $hit = [NativeInputProbe]::WindowFromPoint($screenPoint)
    $clientPoint = $screenPoint
    [NativeInputProbe]::MapWindowPoints([IntPtr]::Zero, $main, [ref]$clientPoint, 1) | Out-Null
    $child = [NativeInputProbe]::ChildWindowFromPointEx(
        $main,
        $clientPoint,
        0x0001 -bor 0x0002 -bor 0x0004)
    [pscustomobject]@{
        Name = $entry.Key
        Screen = "$($screenPoint.X),$($screenPoint.Y)"
        WindowFromPoint = Get-NativeWindow $hit
        ChildWindowFromPointEx = Get-NativeWindow $child
    }
}

$currentThread = [NativeInputProbe]::GetCurrentThreadId()
$targetThread = [NativeInputProbe]::GetWindowThreadProcessId($main, [IntPtr]::Zero)
$attached = $currentThread -ne $targetThread -and
    [NativeInputProbe]::AttachThreadInput($currentThread, $targetThread, $true)
try {
    $focus = [NativeInputProbe]::GetFocus()
}
finally {
    if ($attached) {
        [NativeInputProbe]::AttachThreadInput($currentThread, $targetThread, $false) | Out-Null
    }
}

$cursor = [ProbePoint]::new(0, 0)
[NativeInputProbe]::GetCursorPos([ref]$cursor) | Out-Null
$descendants = foreach ($window in [NativeInputProbe]::Descendants($main)) {
    Get-NativeWindow $window
}
[NativeInputProbe]::SetWindowPos(
    $main, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null

[pscustomobject]@{
    ProcessId = $ProcessId
    WebViewPromoted = [bool]$PromoteWebView
    MainWindow = Get-NativeWindow $main
    ReaderSurface = "$readerLeft,$readerTop,$readerWidth,$readerHeight"
    Foreground = Get-NativeWindow ([NativeInputProbe]::GetForegroundWindow())
    Focus = Get-NativeWindow $focus
    Cursor = "$($cursor.X),$($cursor.Y)"
    Hits = @($hits)
    Descendants = @($descendants)
} | ConvertTo-Json -Depth 6
