param(
    [int]$ProcessId = 0,
    [int]$StartDelayMilliseconds = 0,
    [string]$ScreenshotPath,
    [switch]$LayoutOnly,
    [switch]$WebViewOnly,
    [switch]$SkipInputHitTest
)

$ErrorActionPreference = 'Stop'
if ($StartDelayMilliseconds -gt 0) {
    Start-Sleep -Milliseconds $StartDelayMilliseconds
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public struct VDictNativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int Width { get { return Math.Max(0, Right - Left); } }
    public int Height { get { return Math.Max(0, Bottom - Top); } }
}

public struct VDictNativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct VDictMouseInput
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
public struct VDictInputUnion
{
    [FieldOffset(0)] public VDictMouseInput Mouse;
}

[StructLayout(LayoutKind.Sequential)]
public struct VDictInput
{
    public uint Type;
    public VDictInputUnion Data;
}

public static class VDictTestInput
{
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr window, bool altTab);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, VDictInput[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref VDictNativePoint point);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out VDictNativeRect rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(VDictNativePoint point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    public static void CtrlKey(byte virtualKey)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    public static void PostKey(IntPtr window, byte virtualKey)
    {
        uint scanCode = MapVirtualKey(virtualKey, 0);
        long extended = virtualKey == 0x26 || virtualKey == 0x28 ||
            virtualKey == 0x25 || virtualKey == 0x27 ? 1L << 24 : 0;
        long keyDown = 1L | ((long)scanCode << 16) | extended;
        long keyUp = keyDown | (1L << 30) | (1L << 31);
        PostMessage(window, 0x0100, (UIntPtr)virtualKey, (IntPtr)keyDown);
        PostMessage(window, 0x0101, (UIntPtr)virtualKey, (IntPtr)keyUp);
    }

    public static void RestoreWindow(IntPtr window)
    {
        PostMessage(window, 0x0112, (UIntPtr)0xF120u, IntPtr.Zero);
        ShowWindow(window, 9);
    }

    public static void Key(byte virtualKey)
    {
        uint extended = virtualKey == 0x26 || virtualKey == 0x28 ||
            virtualKey == 0x25 || virtualKey == 0x27 ? 0x0001u : 0u;
        keybd_event(virtualKey, 0, extended, UIntPtr.Zero);
        keybd_event(virtualKey, 0, extended | 0x0002u, UIntPtr.Zero);
    }

    public static void CtrlWheel(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        SendMouse(0x0800, unchecked((uint)delta));
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[]
        {
            MouseInput(0x0002, 0),
            MouseInput(0x0004, 0)
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<VDictInput>()) != inputs.Length)
        {
            throw new InvalidOperationException(
                "SendInput click failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
        }
    }

    private static VDictInput MouseInput(uint flags, uint data)
    {
        return new VDictInput
        {
            Type = 0,
            Data = new VDictInputUnion
            {
                Mouse = new VDictMouseInput
                {
                    MouseData = data,
                    Flags = flags
                }
            }
        };
    }

    private static void SendMouse(uint flags, uint data)
    {
        var inputs = new[] { MouseInput(flags, data) };
        if (SendInput(1, inputs, Marshal.SizeOf<VDictInput>()) != 1)
        {
            throw new InvalidOperationException(
                "SendInput mouse event failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
        }
    }

    public static void Drag(int startX, int startY, int endX, int endY)
    {
        SetCursorPos(startX, startY);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        for (int step = 1; step <= 8; step++)
        {
            int x = startX + (endX - startX) * step / 8;
            int y = startY + (endY - startY) * step / 8;
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(20);
        }
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static IntPtr[] GetDescendantWindows(IntPtr parent)
    {
        var windows = new List<IntPtr>();
        EnumChildWindows(parent, (window, state) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static string GetWindowClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    public static IntPtr WindowAt(int x, int y)
    {
        return WindowFromPoint(new VDictNativePoint { X = x, Y = y });
    }

    public static long WindowStyle(IntPtr window)
    {
        return GetWindowLongPtr(window, -16).ToInt64();
    }

    public static long WindowExStyle(IntPtr window)
    {
        return GetWindowLongPtr(window, -20).ToInt64();
    }

    public static long HitTestAt(IntPtr window, int x, int y)
    {
        long packed = ((long)(y & 0xffff) << 16) | (uint)(x & 0xffff);
        UIntPtr result;
        SendMessageTimeout(window, 0x0084, UIntPtr.Zero, (IntPtr)packed, 2, 1000, out result);
        return unchecked((long)result.ToUInt64());
    }

    public static bool IsDescendantOf(IntPtr window, IntPtr ancestor)
    {
        for (IntPtr current = window; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    public static IntPtr GetFocusForWindowThread(IntPtr window)
    {
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attached = currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            return GetFocus();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    public static IntPtr GetCaptureForWindowThread(IntPtr window)
    {
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attached = currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            return GetCapture();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    public static bool FocusWindow(IntPtr window)
    {
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attached = currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            SetFocus(window);
            return GetFocus() == window;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    public static void PostClick(IntPtr window, int screenX, int screenY)
    {
        var point = new VDictNativePoint { X = screenX, Y = screenY };
        ScreenToClient(window, ref point);
        long packed = ((long)(point.Y & 0xffff) << 16) | (uint)(point.X & 0xffff);
        PostMessage(window, 0x0201, (UIntPtr)1, (IntPtr)packed);
        PostMessage(window, 0x0202, UIntPtr.Zero, (IntPtr)packed);
    }

    public static bool Activate(IntPtr window)
    {
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(window, IntPtr.Zero);
        bool attached = currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(window, 9);
            BringWindowToTop(window);
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SwitchToThisWindow(window, true);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }
}
'@

if ($ProcessId -eq 0) {
    $ProcessId = Get-Process VDict |
        Sort-Object StartTime -Descending |
        Select-Object -First 1 -ExpandProperty Id
}

$processCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $ProcessId)
$topLevelCandidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $processCondition)
$root = $topLevelCandidates |
    Where-Object { $_.Current.Name -eq 'VDict' } |
    Select-Object -First 1
if ($null -eq $root) {
    throw "VDict UI Automation root was not found for process $ProcessId."
}
$windowHandle = [IntPtr]$root.Current.NativeWindowHandle
[VDictTestInput]::Activate($windowHandle) | Out-Null
Start-Sleep -Milliseconds 200

function Find-ById([string]$automationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    $element = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $element) {
        throw "Automation element '$automationId' was not found."
    }

    return $element
}

function Wait-Until([scriptblock]$condition, [string]$failure, [int]$milliseconds = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    do {
        if (& $condition) {
            return
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $failure
}

function Wait-ForValue([scriptblock]$condition, [string]$failure, [int]$milliseconds = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    do {
        $value = & $condition
        if ($null -ne $value) {
            return $value
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $failure
}

function Get-SelectionName($list) {
    $pattern = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $selection = $pattern.Current.GetSelection()
    if ($selection.Count -eq 0) {
        return $null
    }

    return $selection[0].Current.Name
}

function Get-FirstItemName($list) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $item = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    return $item.Current.Name
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Focus-ByClick($element) {
    [VDictTestInput]::Activate($windowHandle) | Out-Null
    $rect = $element.Current.BoundingRectangle
    [VDictTestInput]::Click(
        [int]($rect.Left + $rect.Width / 2),
        [int]($rect.Top + $rect.Height / 2))
    Wait-Until { $element.Current.HasKeyboardFocus } "Element '$($element.Current.AutomationId)' did not receive focus."
}

function Get-Right($rect) { return $rect.Left + $rect.Width }

function Get-Bottom($rect) { return $rect.Top + $rect.Height }

function Test-Intersects($left, $right) {
    return $left.Left -lt (Get-Right $right) -and
        (Get-Right $left) -gt $right.Left -and
        $left.Top -lt (Get-Bottom $right) -and
        (Get-Bottom $left) -gt $right.Top
}

function Get-WebViewWindow([IntPtr]$main, $expectedRect) {
    $best = $null
    $bestScore = 0.0
    foreach ($child in [VDictTestInput]::GetDescendantWindows($main)) {
        if (-not [VDictTestInput]::IsWindowVisible($child)) {
            continue
        }
        $className = [VDictTestInput]::GetWindowClass($child)
        if ($className -notlike 'Chrome_*') {
            continue
        }
        $native = [VDictNativeRect]::new()
        if (-not [VDictTestInput]::GetWindowRect($child, [ref]$native) -or
            $native.Width -le 0 -or $native.Height -le 0) {
            continue
        }
        $left = [Math]::Max($expectedRect.Left, $native.Left)
        $top = [Math]::Max($expectedRect.Top, $native.Top)
        $right = [Math]::Min((Get-Right $expectedRect), $native.Right)
        $bottom = [Math]::Min((Get-Bottom $expectedRect), $native.Bottom)
        $intersection = [Math]::Max(0, $right - $left) * [Math]::Max(0, $bottom - $top)
        $parent = [VDictTestInput]::GetParent($child)
        $score = $intersection + $(if ($parent -eq $main) { 1.0e15 } else { 0 })
        if ($score -gt $bestScore) {
            $bestScore = $score
            $best = [pscustomobject]@{
                Hwnd = $child
                ParentHwnd = $parent
                ClassName = $className
                Left = [double]$native.Left
                Top = [double]$native.Top
                Width = [double]$native.Width
                Height = [double]$native.Height
            }
        }
    }
    return $best
}

function Assert-WebViewLayout([string]$phase) {
    $readerBounds = $reader.Current.BoundingRectangle
    $webViewWindow = Get-WebViewWindow $windowHandle $reader.Current.BoundingRectangle
    if ($null -eq $webViewWindow) {
        $webViewWindow = [pscustomobject]@{
            Hwnd = [IntPtr]::Zero
            ParentHwnd = $windowHandle
            ClassName = 'XamlWebView2Composition'
            Left = [double]$readerBounds.Left
            Top = [double]$readerBounds.Top
            Width = [double]$readerBounds.Width
            Height = [double]$readerBounds.Height
        }
    }
    $tolerance = 1.0
    $edgeDeltas = @(
        [Math]::Abs($webViewWindow.Left - $readerBounds.Left),
        [Math]::Abs($webViewWindow.Top - $readerBounds.Top),
        [Math]::Abs((Get-Right $webViewWindow) - (Get-Right $readerBounds)),
        [Math]::Abs((Get-Bottom $webViewWindow) - (Get-Bottom $readerBounds))
    )
    if (($edgeDeltas | Measure-Object -Maximum).Maximum -gt $tolerance) {
        throw "WebView edges differ from ReaderSurface by more than one physical pixel during $phase. Reader=$readerBounds WebView=$($webViewWindow | ConvertTo-Json -Compress) Deltas=$($edgeDeltas -join ',')."
    }
    if ($webViewWindow.Left -lt $readerBounds.Left - $tolerance -or
        $webViewWindow.Top -lt $readerBounds.Top - $tolerance -or
        (Get-Right $webViewWindow) -gt (Get-Right $readerBounds) + $tolerance -or
        (Get-Bottom $webViewWindow) -gt (Get-Bottom $readerBounds) + $tolerance) {
        throw "WebView is not contained by ReaderSurface during $phase."
    }
    foreach ($obstacleId in @('AppTitleBar', 'SearchToolbar', 'CandidatePane', 'CandidateDivider', 'StatusBar')) {
        $obstacleCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $obstacleId)
        $obstacle = $root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $obstacleCondition)
        if ($null -eq $obstacle -and
            $webViewWindow.ClassName -eq 'XamlWebView2Composition' -and
            $obstacleId -eq 'StatusBar') {
            continue
        }
        if ($null -eq $obstacle) {
            throw "Automation element '$obstacleId' was not found during $phase."
        }
        if (Test-Intersects $webViewWindow $obstacle.Current.BoundingRectangle) {
            throw "WebView intersects '$obstacleId' during $phase."
        }
    }
    $hitTestScale = [VDictTestInput]::GetDpiForWindow($windowHandle) / 96.0
    $inset = [Math]::Max(4.0, 8.0 * $hitTestScale)
    $hitLeft = [double]$readerBounds.Left
    $hitTop = [double]$readerBounds.Top
    $hitWidth = [double]$readerBounds.Width
    $hitHeight = [double]$readerBounds.Height
    $hitPoints = [ordered]@{
        Center = @(($hitLeft + $hitWidth / 2), ($hitTop + $hitHeight / 2))
        TopLeft = @(($hitLeft + $inset), ($hitTop + $inset))
        TopRight = @(($hitLeft + $hitWidth - $inset), ($hitTop + $inset))
        BottomLeft = @(($hitLeft + $inset), ($hitTop + $hitHeight - $inset))
        BottomRight = @(($hitLeft + $hitWidth - $inset), ($hitTop + $hitHeight - $inset))
    }
    $hitClasses = @()
    if (-not $SkipInputHitTest) {
        [VDictTestInput]::SetWindowPos(
            $windowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
        try {
            foreach ($hitPoint in $hitPoints.GetEnumerator()) {
                $hit = [VDictTestInput]::WindowAt(
                    [int]$hitPoint.Value[0],
                    [int]$hitPoint.Value[1])
                $hitClass = [VDictTestInput]::GetWindowClass($hit)
                $isWindowedWebViewHit = $hitClass -like 'Chrome_*'
                $isXamlCompositionHit =
                    $webViewWindow.ClassName -eq 'XamlWebView2Composition' -and
                    $hitClass -eq 'Microsoft.UI.Content.DesktopChildSiteBridge'
                if ((-not $isWindowedWebViewHit -and -not $isXamlCompositionHit) -or
                    -not [VDictTestInput]::IsDescendantOf($hit, $windowHandle)) {
                    throw "WebView input hit test failed at $($hitPoint.Key) during $phase. Hit=$hit Class=$hitClass Reader=$readerBounds."
                }
                $hitClasses += $hitClass
            }
        }
        finally {
            [VDictTestInput]::SetWindowPos(
                $windowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x0013) | Out-Null
        }
    }
    else {
        $hitClasses = @('skipped-lock-screen')
    }
    return [pscustomobject]@{
        Phase = $phase
        Reader = "$($readerBounds.Left),$($readerBounds.Top),$($readerBounds.Width),$($readerBounds.Height)"
        WebView = "$($webViewWindow.Left),$($webViewWindow.Top),$($webViewWindow.Width),$($webViewWindow.Height)"
        ClassName = $webViewWindow.ClassName
        MaxEdgeDelta = ($edgeDeltas | Measure-Object -Maximum).Maximum
        HitClasses = $hitClasses
    }
}

function Save-WindowScreenshot([IntPtr]$window, [string]$path) {
    Add-Type -AssemblyName System.Drawing
    $native = [VDictNativeRect]::new()
    if (-not [VDictTestInput]::GetWindowRect($window, [ref]$native)) {
        throw 'Unable to read window rectangle for screenshot.'
    }
    $fullPath = [IO.Path]::GetFullPath($path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new($native.Width, $native.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($native.Left, $native.Top, 0, 0, $bitmap.Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($fullPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
    return $fullPath
}

$search = Find-ById 'SearchBox'
$candidates = Find-ById 'CandidateList'
$reader = Find-ById 'ReaderSurface'
$zoomText = $null
$zoomOut = $null
$zoomIn = $null
$zoomReset = $null
if (-not $WebViewOnly -and -not $LayoutOnly) {
    $zoomText = Find-ById 'ZoomPercentText'
    $zoomOut = Find-ById 'ZoomOutButton'
    $zoomIn = Find-ById 'ZoomInButton'
    $zoomReset = Find-ById 'ZoomResetButton'
}
$value = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)

$layoutChecks = @()
$layoutChecks += Assert-WebViewLayout 'initial'
$mainWindowPattern = $root.GetCurrentPattern(
    [System.Windows.Automation.WindowPattern]::Pattern)
$mainWindowPattern.SetWindowVisualState(
    [System.Windows.Automation.WindowVisualState]::Maximized)
Start-Sleep -Milliseconds 350
$layoutChecks += Assert-WebViewLayout 'maximized'
$mainWindowPattern.SetWindowVisualState(
    [System.Windows.Automation.WindowVisualState]::Normal)
[VDictTestInput]::Activate($windowHandle) | Out-Null
Wait-Until {
    [VDictTestInput]::IsWindowVisible($windowHandle) -and
        $root.Current.BoundingRectangle.Width -gt 0
} 'Main window did not restore after maximization.'
Start-Sleep -Milliseconds 250
$layoutChecks += Assert-WebViewLayout 'restored-after-maximize'
$dpiScale = [VDictTestInput]::GetDpiForWindow($windowHandle) / 96.0
$windowRect = [VDictNativeRect]::new()
[VDictTestInput]::GetWindowRect($windowHandle, [ref]$windowRect) | Out-Null
[VDictTestInput]::SetWindowPos(
    $windowHandle,
    [IntPtr]::Zero,
    $windowRect.Left,
    $windowRect.Top,
    [int](960 * $dpiScale),
    [int](620 * $dpiScale),
    0x0014) | Out-Null
Start-Sleep -Milliseconds 350
$layoutChecks += Assert-WebViewLayout 'resized'

$divider = Find-ById 'CandidateDivider'
$candidateBefore = (Find-ById 'CandidatePane').Current.BoundingRectangle.Width
$divider.SetFocus()
Wait-Until { $divider.Current.HasKeyboardFocus } 'Candidate divider did not accept keyboard focus.'
for ($step = 0; $step -lt 4; $step++) {
    [VDictTestInput]::PostKey($windowHandle, 0x27)
    Start-Sleep -Milliseconds 50
}
Start-Sleep -Milliseconds 250
$candidateAfter = (Find-ById 'CandidatePane').Current.BoundingRectangle.Width
if ([Math]::Abs($candidateAfter - $candidateBefore) -lt 8 * $dpiScale) {
    throw "Candidate divider keyboard adjustment did not change the pane width. Before=$candidateBefore After=$candidateAfter Foreground=$([VDictTestInput]::GetForegroundWindow()) Main=$windowHandle."
}
$layoutChecks += Assert-WebViewLayout 'candidate-pane-adjusted'

$capturedScreenshot = $null
if (-not [string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    [VDictTestInput]::Activate($windowHandle) | Out-Null
    Start-Sleep -Milliseconds 100
    $capturedScreenshot = Save-WindowScreenshot $windowHandle $ScreenshotPath
}

if ($LayoutOnly) {
    [pscustomobject]@{
        ProcessId = $ProcessId
        DpiScale = $dpiScale
        LayoutChecks = $layoutChecks
        CandidatePaneWidthBefore = $candidateBefore
        CandidatePaneWidthAfter = $candidateAfter
        Screenshot = $capturedScreenshot
        LayoutOnly = $true
    } | ConvertTo-Json -Depth 5
    return
}

if ($WebViewOnly) {
    if (-not [VDictTestInput]::Activate($windowHandle)) {
        throw "VDict did not become foreground before WebView input. Foreground=$([VDictTestInput]::GetForegroundWindow()) Main=$windowHandle."
    }
    Start-Sleep -Milliseconds 100
    [VDictTestInput]::SetWindowPos(
        $windowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
    try {
    $value.SetValue('can')
    $search.SetFocus()
    [VDictTestInput]::Key(0x0D)
    Wait-Until {
        $reader.Current.HelpText -eq 'Rendered:can'
    } 'Dictionary fixture did not render before WebView input checks.' 15000
    $readerRect = $reader.Current.BoundingRectangle
    $layoutChecks += Assert-WebViewLayout 'before-real-click'
    $captureBeforeClick = [VDictTestInput]::GetCaptureForWindowThread($windowHandle)
    $captureBeforeClickClass = [VDictTestInput]::GetWindowClass($captureBeforeClick)
    [VDictTestInput]::Click(
        [int]($readerRect.Left + $readerRect.Width / 2),
        [int]($readerRect.Top + $readerRect.Height / 2))
    Start-Sleep -Milliseconds 200
    Wait-Until {
        $reader.Current.HelpText -like '*Input:pointerdown*'
    } 'Real click did not reach the WebView DOM bridge.'
    $layoutChecks += Assert-WebViewLayout 'after-real-click'
    $centerHit = [VDictTestInput]::WindowAt(
        [int]($readerRect.Left + $readerRect.Width / 2),
        [int]($readerRect.Top + $readerRect.Height / 2))
    $centerHitClass = [VDictTestInput]::GetWindowClass($centerHit)
    $centerNativeHitTest = [VDictTestInput]::HitTestAt(
        $centerHit,
        [int]($readerRect.Left + $readerRect.Width / 2),
        [int]($readerRect.Top + $readerRect.Height / 2))
    $centerHitStyle = [VDictTestInput]::WindowStyle($centerHit)
    $centerHitExStyle = [VDictTestInput]::WindowExStyle($centerHit)
    $nativeChain = @()
    for ($nativeWindow = $centerHit;
         $nativeWindow -ne [IntPtr]::Zero -and $nativeWindow -ne $windowHandle;
         $nativeWindow = [VDictTestInput]::GetParent($nativeWindow)) {
        $nativeClass = [VDictTestInput]::GetWindowClass($nativeWindow)
        $nativeHitTest = [VDictTestInput]::HitTestAt(
            $nativeWindow,
            [int]($readerRect.Left + $readerRect.Width / 2),
            [int]($readerRect.Top + $readerRect.Height / 2))
        $nativeStyle = [VDictTestInput]::WindowStyle($nativeWindow)
        $nativeExStyle = [VDictTestInput]::WindowExStyle($nativeWindow)
        $nativeChain += "$nativeClass`:HT=$nativeHitTest`:" +
            "S=0x$($nativeStyle.ToString('X'))`:E=0x$($nativeExStyle.ToString('X'))"
    }
    $focusedHwnd = [VDictTestInput]::GetFocusForWindowThread($windowHandle)
    $focusedClass = [VDictTestInput]::GetWindowClass($focusedHwnd)
    $webViewFocusedHwnd = [VDictTestInput]::GetFocusForWindowThread($centerHit)
    $webViewFocusedClass = [VDictTestInput]::GetWindowClass($webViewFocusedHwnd)
    [VDictTestInput]::CtrlWheel(
        [int]($readerRect.Left + $readerRect.Width / 2),
        [int]($readerRect.Top + $readerRect.Height / 2),
        120)
    Wait-Until { $reader.Current.HelpText -like '*Input:wheel*' } (
        "Real Ctrl+wheel did not reach the WebView DOM bridge. " +
        "FocusClass=$focusedClass; ReaderHelpText=$($reader.Current.HelpText); " +
        "CenterHitClass=$centerHitClass; WebViewThreadFocusClass=$webViewFocusedClass; " +
        "CenterNativeHitTest=$centerNativeHitTest; Style=0x$($centerHitStyle.ToString('X')); " +
        "ExStyle=0x$($centerHitExStyle.ToString('X')); " +
        "NativeChain=$($nativeChain -join '|'); " +
        "CaptureBeforeClick=$captureBeforeClickClass/$captureBeforeClick; " +
        "HitClasses=$(($layoutChecks[-1].HitClasses) -join ','); " +
        "Foreground=$([VDictTestInput]::GetForegroundWindow()); Main=$windowHandle.")
    [VDictTestInput]::CtrlKey(0x30)
    }
    finally {
        [VDictTestInput]::SetWindowPos(
            $windowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x0013) | Out-Null
    }
    [pscustomobject]@{
        ProcessId = $ProcessId
        NativeHitTests = 'passed'
        ClickFocusHwndClass = $focusedClass
        ZoomKeyboard = 'passed'
        ZoomWheelDomBridge = 'passed'
        FinalInput = $reader.Current.HelpText
        DpiScale = $dpiScale
        LayoutChecks = $layoutChecks
        Screenshot = $capturedScreenshot
        WebViewOnly = $true
    } | ConvertTo-Json -Depth 5
    return
}

$value.SetValue('ca')
$value.SetValue('can')
Wait-Until { (Get-FirstItemName $candidates) -eq 'can' } 'Candidates did not refresh to the edited text.'
Wait-Until { $null -eq (Get-SelectionName $candidates) } 'Editing did not clear explicit candidate selection.'
Focus-ByClick $search
[VDictTestInput]::Key(0x58)
Wait-Until { $value.Current.Value -ne 'can' } 'Injected keyboard input did not reach the focused search box.'
$value.SetValue('can')
Wait-Until { $null -eq (Get-SelectionName $candidates) } 'Editing after injected input did not clear selection.'
[VDictTestInput]::Key(0x28)
Wait-Until { (Get-SelectionName $candidates) -eq 'can' } 'Down did not select the first candidate.'
if (-not $search.Current.HasKeyboardFocus) {
    throw 'Search box lost focus after Down.'
}
[VDictTestInput]::Key(0x26)
Wait-Until {
    $selected = Get-SelectionName $candidates
    $null -ne $selected -and $selected -ne 'can'
} 'Up did not wrap from the first candidate to the last candidate.'

$value.SetValue('ca')
$value.SetValue('can')
Focus-ByClick $search
[VDictTestInput]::Key(0x28)
[VDictTestInput]::Key(0x0D)
Wait-Until { $search.Current.HasKeyboardFocus } 'Search box focus was not restored after candidate query.'

Invoke-Element $zoomReset
Wait-Until { $zoomText.Current.Name -eq '100%' } 'Zoom reset button did not set 100%.'
Invoke-Element $zoomIn
Wait-Until { $zoomText.Current.Name -eq '110%' } 'Zoom in button did not set 110%.'
Invoke-Element $zoomOut
Wait-Until { $zoomText.Current.Name -eq '100%' } 'Zoom out button did not return to 100%.'

$readerRect = $reader.Current.BoundingRectangle
[VDictTestInput]::Click(
    [int]($readerRect.Left + $readerRect.Width / 2),
    [int]($readerRect.Top + $readerRect.Height / 2))
Start-Sleep -Milliseconds 100
[VDictTestInput]::CtrlKey(0x6B)
Wait-Until { $zoomText.Current.Name -eq '110%' } 'Ctrl+Plus did not share the zoom state.'
[VDictTestInput]::CtrlWheel(
    [int]($reader.Current.BoundingRectangle.Left + $reader.Current.BoundingRectangle.Width / 2),
    [int]($reader.Current.BoundingRectangle.Top + $reader.Current.BoundingRectangle.Height / 2),
    120)
Wait-Until { $zoomText.Current.Name -eq '120%' } 'Ctrl+wheel did not share the zoom state.'
[VDictTestInput]::CtrlKey(0x30)
Wait-Until { $zoomText.Current.Name -eq '100%' } 'Ctrl+0 did not reset zoom.'

[pscustomobject]@{
    ProcessId = $ProcessId
    CandidateDown = 'can'
    CandidateWrap = 'passed'
    SearchFocusRestored = $search.Current.HasKeyboardFocus
    ZoomButtons = 'passed'
    ZoomKeyboard = 'passed'
    ZoomWheel = 'passed'
    FinalZoom = $zoomText.Current.Name
    DpiScale = $dpiScale
    LayoutChecks = $layoutChecks
    CandidatePaneWidthBefore = $candidateBefore
    CandidatePaneWidthAfter = $candidateAfter
    Screenshot = $capturedScreenshot
} | ConvertTo-Json -Depth 5
