[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PortableDirectory,

    [Parameter()]
    [ValidateRange(5, 120)]
    [int] $TimeoutSeconds = 30,

    [Parameter()]
    [string] $Query,

    [Parameter()]
    [string] $ExpectedReaderText,

    [Parameter()]
    [string] $SecondQuery,

    [Parameter()]
    [string] $ExpectedReaderTextAfterSecondQuery,

    [Parameter()]
    [switch] $InvokeBack,

    [Parameter()]
    [string] $ExpectedReaderTextAfterBack,

    [Parameter()]
    [string] $ExpectedReaderTextAfterClick,

    [Parameter()]
    [string] $ScreenshotDirectory,

    [Parameter()]
    [string] $InvokeReaderText,

    [Parameter()]
    [ValidateRange(-1, 10000)]
    [int] $ReaderClickX = -1,

    [Parameter()]
    [ValidateRange(-1, 10000)]
    [int] $ReaderClickY = -1,

    [Parameter()]
    [ValidateSet('Left', 'Right')]
    [string] $ReaderClickButton = 'Left',

    [Parameter()]
    [ValidateRange(0, 4)]
    [double] $WebViewScale = 0,

    [Parameter()]
    [ValidateRange(-10000, 100000)]
    [int] $WindowX = 100000,

    [Parameter()]
    [ValidateRange(-10000, 100000)]
    [int] $WindowY = 100000,

    [Parameter()]
    [ValidateRange(760, 1600)]
    [int] $SettingsWidthDip = 760,

    [Parameter()]
    [ValidateRange(520, 1200)]
    [int] $SettingsHeightDip = 520,

    [Parameter()]
    [switch] $SkipSettingsValidation,

    [Parameter()]
    [ValidateSet('zh-CN', 'en-US')]
    [string] $ExpectedUiLanguage = 'zh-CN'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$settingsAutomationName = if ($ExpectedUiLanguage -eq 'en-US') {
    'Settings'
}
else {
    [string]::Concat([char]0x8BBE, [char]0x7F6E)
}
$openNavigationName = if ($ExpectedUiLanguage -eq 'en-US') {
    'Open Navigation'
}
else {
    -join ([char[]]@(25171, 24320, 23548, 33322))
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public struct VDictSmokeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int Width { get { return Math.Max(0, Right - Left); } }
    public int Height { get { return Math.Max(0, Bottom - Top); } }
}

public static class VDictSmokeNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out VDictSmokeRect rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public static IntPtr[] GetTopLevelWindows(uint processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, state) =>
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(hwnd, out ownerProcessId);
            if (ownerProcessId == processId)
            {
                windows.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    public static IntPtr FindLargestVisibleChromeDescendant(IntPtr parent)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(parent, (hwnd, state) =>
        {
            var name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            VDictSmokeRect rect;
            if (name.ToString().StartsWith("Chrome_", StringComparison.Ordinal) &&
                IsWindowVisible(hwnd) && GetWindowRect(hwnd, out rect))
            {
                long area = (long)rect.Width * rect.Height;
                if (area > bestArea)
                {
                    best = hwnd;
                    bestArea = area;
                }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Condition,

        [Parameter(Mandatory)]
        [string] $FailureMessage
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = & $Condition
        if ($null -ne $value) {
            return ,$value
        }

        Start-Sleep -Milliseconds 100
    }

    throw $FailureMessage
}

function Get-ProcessWindows {
    param([Parameter(Mandatory)][int] $ProcessId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-DescendantByName {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)][string] $Name,
        [System.Windows.Automation.ControlType] $ControlType
    )
    $conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name))
    if ($null -ne $ControlType) {
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    }
    $condition = if ($conditions.Count -eq 1) {
        $conditions[0]
    }
    else {
        [System.Windows.Automation.AndCondition]::new($conditions.ToArray())
    }
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Find-NavigationItem {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)][string] $Name
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    foreach ($candidate in $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)) {
        $pattern = $null
        if ($candidate.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
            return $candidate
        }
    }
    return $null
}

function Find-DescendantByAutomationId {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)][string] $AutomationId
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Test-ComboBoxFirstPointerClick {
    param(
        [Parameter(Mandatory)] $Root,
        [Parameter(Mandatory)][string] $AutomationId,
        [Parameter(Mandatory)][IntPtr] $WindowHandle
    )

    $comboBox = Find-DescendantByAutomationId `
        -Root $Root `
        -AutomationId $AutomationId
    if ($null -eq $comboBox) {
        throw "ComboBox '$AutomationId' was not exposed for pointer validation."
    }
    if ($comboBox.Current.IsOffscreen) {
        $scrollItem = $null
        if ($comboBox.TryGetCurrentPattern(
                [System.Windows.Automation.ScrollItemPattern]::Pattern,
                [ref]$scrollItem)) {
            $scrollItem.ScrollIntoView()
            Start-Sleep -Milliseconds 150
        }
    }
    if ($comboBox.Current.IsOffscreen) {
        $settingsScroller = Find-DescendantByAutomationId `
            -Root $Root `
            -AutomationId 'SettingsScrollViewer'
        $scrollPattern = $null
        if ($null -ne $settingsScroller -and
            $settingsScroller.TryGetCurrentPattern(
                [System.Windows.Automation.ScrollPattern]::Pattern,
                [ref]$scrollPattern)) {
            for ($scrollAttempt = 0;
                $scrollAttempt -lt 10 -and $comboBox.Current.IsOffscreen;
                $scrollAttempt++) {
                if ($scrollPattern.Current.VerticallyScrollable) {
                    $scrollPattern.Scroll(
                        [System.Windows.Automation.ScrollAmount]::NoAmount,
                        [System.Windows.Automation.ScrollAmount]::LargeIncrement)
                    Start-Sleep -Milliseconds 100
                }
            }
        }
    }
    if ($comboBox.Current.IsOffscreen) {
        throw "ComboBox '$AutomationId' was not visible for pointer validation."
    }

    $expandCollapse = $null
    if (-not $comboBox.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$expandCollapse)) {
        throw "ComboBox '$AutomationId' did not expose ExpandCollapsePattern."
    }
    if ($expandCollapse.Current.ExpandCollapseState -eq
        [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $expandCollapse.Collapse()
        Start-Sleep -Milliseconds 100
    }

    [VDictSmokeNative]::ShowWindow($WindowHandle, 9) | Out-Null
    [VDictSmokeNative]::BringWindowToTop($WindowHandle) | Out-Null
    [VDictSmokeNative]::SetForegroundWindow($WindowHandle) | Out-Null
    Start-Sleep -Milliseconds 100

    $bounds = $comboBox.Current.BoundingRectangle
    $clickX = [int][Math]::Round($bounds.Left + ($bounds.Width / 2))
    $clickY = [int][Math]::Round($bounds.Top + ($bounds.Height / 2))
    if (-not [VDictSmokeNative]::SetCursorPos($clickX, $clickY)) {
        throw "Unable to position the pointer over ComboBox '$AutomationId'."
    }
    Start-Sleep -Milliseconds 75
    [VDictSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [VDictSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)

    $expanded = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 25
        if ($expandCollapse.Current.ExpandCollapseState -eq
            [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $expanded = $true
            break
        }
    }
    if (-not $expanded) {
        throw "ComboBox '$AutomationId' did not open from its first pointer click."
    }

    $expandCollapse.Collapse()
    Start-Sleep -Milliseconds 100
    return $true
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory)][IntPtr] $Window,
        [Parameter(Mandatory)][string] $Path
    )
    Add-Type -AssemblyName System.Drawing
    $rect = [VDictSmokeRect]::new()
    if (-not [VDictSmokeNative]::GetWindowRect($Window, [ref]$rect)) {
        throw 'Unable to get the window rectangle for a full-window screenshot.'
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath)) | Out-Null
    $madeTopmost = [VDictSmokeNative]::SetWindowPos(
        $Window,
        [IntPtr](-1),
        0,
        0,
        0,
        0,
        0x0013)
    Start-Sleep -Milliseconds 150
    $bitmap = [System.Drawing.Bitmap]::new($rect.Width, $rect.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            # PrintWindow omits WebView2's composition surface even when it returns
            # success. The smoke test foregrounds the target window before capture,
            # so screen pixels are the reliable end-to-end rendering evidence here.
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($fullPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
        if ($madeTopmost) {
            [VDictSmokeNative]::SetWindowPos(
                $Window,
                [IntPtr](-2),
                0,
                0,
                0,
                0,
                0x0013) | Out-Null
        }
    }
    return $fullPath
}

$portableRoot = (Resolve-Path -LiteralPath $PortableDirectory).Path
$executable = Join-Path $portableRoot 'VDict.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Portable executable not found: $executable"
}

$startParameters = @{
    FilePath = $executable
    WorkingDirectory = $portableRoot
    PassThru = $true
}
if (-not [string]::IsNullOrWhiteSpace($Query)) {
    $startParameters.ArgumentList = @('--lookup', $Query)
}

$webViewArgumentsName = 'WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'
$previousWebViewArguments =
    [System.Environment]::GetEnvironmentVariable($webViewArgumentsName, 'Process')
try {
    if ($WebViewScale -gt 0) {
        $scaleArgument = "--force-device-scale-factor=$($WebViewScale.ToString(
            [System.Globalization.CultureInfo]::InvariantCulture))"
        $arguments = @($previousWebViewArguments, $scaleArgument) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        [System.Environment]::SetEnvironmentVariable(
            $webViewArgumentsName,
            ($arguments -join ' '),
            'Process')
    }

    $process = Start-Process @startParameters
}
finally {
    [System.Environment]::SetEnvironmentVariable(
        $webViewArgumentsName,
        $previousWebViewArguments,
        'Process')
}

try {
    $mainWindow = Wait-Until -FailureMessage 'MainWindow did not become available.' -Condition {
        $process.Refresh()
        if ($process.HasExited) {
            throw "VDict exited early with code $($process.ExitCode)."
        }

        foreach ($window in Get-ProcessWindows -ProcessId $process.Id) {
            if ($window.Current.Name -in @('VDict', 'VDict.exe')) {
                return $window
            }
        }

        return $null
    }

    $mainHwnd = [IntPtr]$mainWindow.Current.NativeWindowHandle
    if ($WindowX -lt 100000 -and $WindowY -lt 100000) {
        $mainRect = [VDictSmokeRect]::new()
        if (-not [VDictSmokeNative]::GetWindowRect($mainHwnd, [ref]$mainRect)) {
            throw 'Unable to read the main window bounds before moving it.'
        }
        [VDictSmokeNative]::SetWindowPos(
            $mainHwnd,
            [IntPtr]::Zero,
            $WindowX,
            $WindowY,
            $mainRect.Width,
            $mainRect.Height,
            0x0014) | Out-Null
        Start-Sleep -Milliseconds 500
    }
    $mainScale = [VDictSmokeNative]::GetDpiForWindow($mainHwnd) / 96.0
    if ([string]::IsNullOrWhiteSpace($ScreenshotDirectory)) {
        $ScreenshotDirectory = Join-Path $portableRoot 'Data\Logs\SmokeScreenshots'
    }
    $browserSurfaceCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'BrowserSurface')
    $browserSurface = Wait-Until -FailureMessage 'WebView XAML host did not become available.' -Condition {
        $candidate = $mainWindow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $browserSurfaceCondition)
        if ($null -ne $candidate) { return $candidate }
        return $null
    }
    $webViewHwnd = [VDictSmokeNative]::FindLargestVisibleChromeDescendant($mainHwnd)
    $webViewHosting = if ($webViewHwnd -ne [IntPtr]::Zero) {
        'WindowedController'
    }
    else {
        'XamlComposition'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedReaderText)) {
        try {
            [void](Wait-Until -FailureMessage 'Expected dictionary text was not rendered.' -Condition {
                foreach ($element in $mainWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                    if (($element.Current.Name -like "*$ExpectedReaderText*" -and
                         -not $element.Current.IsOffscreen) -or
                        $element.Current.HelpText -eq "Rendered:$Query") {
                        return $element
                    }
                }

                return $null
            })
        }
        catch {
            $readerState = @(
                $mainWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition) |
                    Select-Object -First 60 |
                    ForEach-Object {
                        "$($_.Current.ControlType.ProgrammaticName):$($_.Current.Name)"
                    }
            )
            throw "$($_.Exception.Message) UI: $($readerState -join ' | ')"
        }
    }

    $secondQueryScreenshot = $null
    $backNavigationVerified = $false
    if (-not [string]::IsNullOrWhiteSpace($SecondQuery)) {
        $searchBox = Find-DescendantByAutomationId `
            -Root $mainWindow `
            -AutomationId 'SearchBox'
        if ($null -eq $searchBox) {
            throw 'Search box was not exposed to UI Automation.'
        }
        $valuePattern = $searchBox.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.SetValue($SecondQuery)
        $searchButtonName = -join ([char[]]@(26597, 35810))
        $searchButton = Find-DescendantByName `
            -Root $mainWindow `
            -Name $searchButtonName `
            -ControlType ([System.Windows.Automation.ControlType]::Button)
        if ($null -eq $searchButton) {
            throw 'Search button was not exposed to UI Automation.'
        }
        $searchButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-Until -FailureMessage 'Second query was not rendered.' -Condition {
            foreach ($element in $mainWindow.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)) {
                if ((-not [string]::IsNullOrWhiteSpace(
                        $ExpectedReaderTextAfterSecondQuery) -and
                     $element.Current.Name -like
                        "*$ExpectedReaderTextAfterSecondQuery*" -and
                     -not $element.Current.IsOffscreen) -or
                    $element.Current.HelpText -eq "Rendered:$SecondQuery") {
                    return $element
                }
            }
            $historyBack = Find-DescendantByAutomationId `
                -Root $mainWindow `
                -AutomationId 'BackButton'
            if ($null -ne $historyBack -and $historyBack.Current.IsEnabled) {
                return $historyBack
            }
            return $null
        })
        Start-Sleep -Milliseconds 250
        [VDictSmokeNative]::ShowWindow($mainHwnd, 9) | Out-Null
        [VDictSmokeNative]::BringWindowToTop($mainHwnd) | Out-Null
        [VDictSmokeNative]::SetForegroundWindow($mainHwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        $secondQueryScreenshot = Save-WindowScreenshot `
            -Window $mainHwnd `
            -Path (Join-Path $ScreenshotDirectory 'second-query.png')

        if ($InvokeBack) {
            $backButton = Find-DescendantByAutomationId `
                -Root $mainWindow `
                -AutomationId 'BackButton'
            if ($null -eq $backButton -or -not $backButton.Current.IsEnabled) {
                throw 'Back button was not enabled after the second query.'
            }
            $backButton.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            [void](Wait-Until -FailureMessage 'Back navigation did not restore the first query.' -Condition {
                $process.Refresh()
                if ($process.HasExited) {
                    throw "VDict exited during back navigation with code $($process.ExitCode)."
                }
                foreach ($element in $mainWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                    if ((-not [string]::IsNullOrWhiteSpace(
                            $ExpectedReaderTextAfterBack) -and
                         $element.Current.Name -like "*$ExpectedReaderTextAfterBack*" -and
                         -not $element.Current.IsOffscreen) -or
                        $element.Current.HelpText -eq "Rendered:$Query") {
                        return $element
                    }
                }
                return $null
            })
            $backNavigationVerified = $true
        }
    }

    $readerActionInvoked = $false
    $readerPointerClicked = $false
    $readerClickScreenPosition = $null
    $readerSurfaceBounds = $null
    if ($ReaderClickX -ge 0 -and $ReaderClickY -ge 0) {
        [VDictSmokeNative]::ShowWindow($mainHwnd, 9) | Out-Null
        [VDictSmokeNative]::BringWindowToTop($mainHwnd) | Out-Null
        [VDictSmokeNative]::SetForegroundWindow($mainHwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        $previousDpiContext =
            [VDictSmokeNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
        try {
            $readerSurfaceBounds = $browserSurface.Current.BoundingRectangle
            $clickX = [int][Math]::Round($readerSurfaceBounds.Left + $ReaderClickX)
            $clickY = [int][Math]::Round($readerSurfaceBounds.Top + $ReaderClickY)
            $readerClickScreenPosition = "$clickX,$clickY"
            if (-not [VDictSmokeNative]::SetCursorPos($clickX, $clickY)) {
                throw "Unable to position the pointer over the reader at $clickX,$clickY."
            }
            Start-Sleep -Milliseconds 120
            if ($ReaderClickButton -eq 'Right') {
                [VDictSmokeNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
                [VDictSmokeNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
            }
            else {
                [VDictSmokeNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                [VDictSmokeNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            }
        }
        finally {
            if ($previousDpiContext -ne [IntPtr]::Zero) {
                [VDictSmokeNative]::SetThreadDpiAwarenessContext(
                    $previousDpiContext) | Out-Null
            }
        }
        $readerPointerClicked = $true
        Start-Sleep -Milliseconds 800
        if (-not [string]::IsNullOrWhiteSpace($ExpectedReaderTextAfterClick)) {
            [void](Wait-Until `
                -FailureMessage 'Expected post-click dictionary text was not rendered.' `
                -Condition {
                    foreach ($element in $mainWindow.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition)) {
                        if (($element.Current.Name -like "*$ExpectedReaderTextAfterClick*") -or
                            $element.Current.HelpText -eq "Rendered:$ExpectedReaderTextAfterClick") {
                            return $element
                        }
                    }

                    return $null
                })
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($InvokeReaderText)) {
        $readerAction = Wait-Until `
            -FailureMessage "Reader action '$InvokeReaderText' was not exposed to UI Automation." `
            -Condition {
                foreach ($element in $mainWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                    if ($element.Current.Name -like "*$InvokeReaderText*") {
                        $invokePattern = $null
                        if ($element.TryGetCurrentPattern(
                            [System.Windows.Automation.InvokePattern]::Pattern,
                            [ref]$invokePattern)) {
                            return [pscustomobject]@{
                                Kind = 'Invoke'
                                Pattern = $invokePattern
                            }
                        }
                        $legacyPattern = $null
                        if ($element.TryGetCurrentPattern(
                            [System.Windows.Automation.LegacyIAccessiblePattern]::Pattern,
                            [ref]$legacyPattern)) {
                            return [pscustomobject]@{
                                Kind = 'Legacy'
                                Pattern = $legacyPattern
                            }
                        }
                    }
                }
                return $null
            }
        if ($readerAction.Kind -eq 'Invoke') {
            $readerAction.Pattern.Invoke()
        }
        else {
            $readerAction.Pattern.DoDefaultAction()
        }
        $readerActionInvoked = $true
        Start-Sleep -Milliseconds 800
    }

    [VDictSmokeNative]::ShowWindow($mainHwnd, 9) | Out-Null
    [VDictSmokeNative]::BringWindowToTop($mainHwnd) | Out-Null
    [VDictSmokeNative]::SetForegroundWindow($mainHwnd) | Out-Null
    Start-Sleep -Milliseconds 350
    $mainScreenshot = Save-WindowScreenshot `
        -Window $mainHwnd `
        -Path (Join-Path $ScreenshotDirectory 'main-window.png')

    if ($SkipSettingsValidation) {
        [pscustomobject]@{
            OperatingSystem = [System.Environment]::OSVersion.VersionString
            ProcessId = $process.Id
            MainWindowHwnd = $mainHwnd.ToInt64()
            MainDpiScale = $mainScale
            WebViewScaleRequested = $WebViewScale
            WindowPositionRequested = if ($WindowX -lt 100000 -and $WindowY -lt 100000) {
                "$WindowX,$WindowY"
            } else {
                ''
            }
            WebViewHosting = $webViewHosting
            MainWindowScreenshot = $mainScreenshot
            SecondQueryScreenshot = $secondQueryScreenshot
            QueryRendered = -not [string]::IsNullOrWhiteSpace($ExpectedReaderText)
            SecondQueryRendered = -not [string]::IsNullOrWhiteSpace($SecondQuery)
            BackNavigationVerified = $backNavigationVerified
            PostClickTextRendered =
                -not [string]::IsNullOrWhiteSpace($ExpectedReaderTextAfterClick)
            ReaderActionInvoked = $readerActionInvoked
            ReaderPointerClicked = $readerPointerClicked
            ReaderClickButton = $ReaderClickButton
            ReaderSurfaceBounds = if ($null -ne $readerSurfaceBounds) {
                $readerSurfaceBounds.ToString()
            } else {
                ''
            }
            ReaderClickScreenPosition = $readerClickScreenPosition
            SettingsValidationSkipped = $true
        }
        return
    }

    try {
        $settingsButton = Wait-Until `
            -FailureMessage 'Settings button was not exposed to UI Automation.' `
            -Condition {
                foreach ($element in $mainWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)) {
                    if ($element.Current.Name -eq $settingsAutomationName) {
                        return $element
                    }
                }

                return $null
            }
    }
    catch {
        $available = @(
            $mainWindow.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                Select-Object -First 40 |
                ForEach-Object {
                    "$($_.Current.ControlType.ProgrammaticName):$($_.Current.Name)"
                }
        )
        throw "$($_.Exception.Message) Available: $($available -join ' | ')"
    }

    $invoke = $settingsButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    try {
        $settingsWindow = Wait-Until -FailureMessage 'SettingsWindow did not become available.' -Condition {
            foreach ($candidateHwnd in [VDictSmokeNative]::GetTopLevelWindows($process.Id)) {
                if ($candidateHwnd -ne [IntPtr]::Zero -and
                    $candidateHwnd -ne $mainHwnd -and
                    [VDictSmokeNative]::IsWindowVisible($candidateHwnd) -and
                    [VDictSmokeNative]::GetWindow($candidateHwnd, 4) -eq $mainHwnd) {
                    return [System.Windows.Automation.AutomationElement]::FromHandle(
                        $candidateHwnd)
                }
            }

            return $null
        }
    }
    catch {
        $process.Refresh()
        $processState = if ($process.HasExited) {
            "exited with code $($process.ExitCode)"
        }
        else {
            'still running'
        }
        $windowNames = @(
            Get-ProcessWindows -ProcessId $process.Id |
                ForEach-Object { $_.Current.Name }
        )
        throw "$($_.Exception.Message) Process $processState. Top-level windows: $($windowNames -join ' | ')"
    }

    $ownedSettings = @(
        [VDictSmokeNative]::GetTopLevelWindows($process.Id) |
            Where-Object {
                $candidateHwnd = [IntPtr]$_
                $candidateHwnd -ne [IntPtr]::Zero -and
                $candidateHwnd -ne $mainHwnd -and
                [VDictSmokeNative]::IsWindowVisible($candidateHwnd) -and
                [VDictSmokeNative]::GetWindow($candidateHwnd, 4) -eq $mainHwnd
            }
    )
    if ($ownedSettings.Count -ne 1) {
        throw "Expected one SettingsWindow, found $($ownedSettings.Count)."
    }

    $settingsHwnd = [IntPtr]$settingsWindow.Current.NativeWindowHandle
    $ownerHwnd = [VDictSmokeNative]::GetWindow($settingsHwnd, 4)
    if ($ownerHwnd -ne $mainHwnd) {
        throw 'SettingsWindow owner HWND does not match MainWindow.'
    }

    if ([VDictSmokeNative]::IsWindowEnabled($mainHwnd)) {
        throw 'MainWindow remained enabled while SettingsWindow was open.'
    }

    if ($webViewHosting -eq 'WindowedController') {
        if ([VDictSmokeNative]::IsWindowVisible($webViewHwnd)) {
            throw 'WebView native window remained visible while the owned modal SettingsWindow was open.'
        }
    }
    elseif (-not $browserSurface.Current.IsOffscreen) {
        throw 'WebView XAML host remained visible while the owned modal SettingsWindow was open.'
    }

    $zOrderedWindows = @([VDictSmokeNative]::GetTopLevelWindows($process.Id))
    $settingsZIndex = [Array]::IndexOf($zOrderedWindows, $settingsHwnd)
    $mainZIndex = [Array]::IndexOf($zOrderedWindows, $mainHwnd)
    if ($settingsZIndex -lt 0 -or $mainZIndex -lt 0 -or $settingsZIndex -ge $mainZIndex) {
        throw "SettingsWindow was not above MainWindow in native Z-order. Settings=$settingsZIndex Main=$mainZIndex."
    }

    $settingsRect = [VDictSmokeRect]::new()
    [VDictSmokeNative]::GetWindowRect($settingsHwnd, [ref]$settingsRect) | Out-Null
    $settingsScale = [VDictSmokeNative]::GetDpiForWindow($settingsHwnd) / 96.0
    [VDictSmokeNative]::SetWindowPos(
        $settingsHwnd,
        [IntPtr]::Zero,
        $settingsRect.Left,
        $settingsRect.Top,
        [int]($SettingsWidthDip * $settingsScale),
        [int]($SettingsHeightDip * $settingsScale),
        0x0014) | Out-Null
    Start-Sleep -Milliseconds 350

    $settingsPages = [ordered]@{}
    if ($ExpectedUiLanguage -eq 'en-US') {
        $dictionaryPageName = 'Dictionaries'
        $addDictionaryButtonName = 'Add MDX dictionary'
        $readingPageName = 'Reading and appearance'
        $securityPageName = 'Security and compatibility'
        $dataPageName = 'Data and maintenance'
        $aboutPageName = 'About'
        $settingsPages[$dictionaryPageName] = 'Dictionaries'
        $settingsPages[$readingPageName] = 'Reading & appearance'
        $settingsPages[$securityPageName] = 'Security & compatibility'
        $settingsPages[$dataPageName] = 'Data & maintenance'
        $settingsPages[$aboutPageName] = $aboutPageName
    }
    else {
        $dictionaryPageName = -join ([char[]]@(35789, 24211))
        $addDictionaryButtonName = -join ([char[]]@(28155, 21152, 32, 77, 68, 88, 32, 35789, 20856))
        $readingPageName = -join ([char[]]@(38405, 35835, 19982, 22806, 35266))
        $securityPageName = -join ([char[]]@(23433, 20840, 19982, 20860, 23481))
        $dataPageName = -join ([char[]]@(25968, 25454, 19982, 32500, 25252))
        $aboutPageName = -join ([char[]]@(20851, 20110))
        $settingsPages[$dictionaryPageName] = $dictionaryPageName
        $settingsPages[$readingPageName] = $readingPageName
        $settingsPages[$securityPageName] = $securityPageName
        $settingsPages[$dataPageName] = $dataPageName
        $settingsPages[$aboutPageName] = $aboutPageName
    }
    $visitedPages = @()
    $contentWidthControlFound = $false
    $dictionaryActionsFound = $false
    $dictionarySelectionContainersStable = $null
    $dictionaryPanelsDistinct = $false
    $dictionarySettingsScreenshot = $null
    $comboBoxesValidatedByFirstPointerClick = @()
    foreach ($entry in $settingsPages.GetEnumerator()) {
        $paneToggle = Find-DescendantByAutomationId `
            -Root $settingsWindow `
            -AutomationId 'TogglePaneButton'
        if ($null -ne $paneToggle -and $paneToggle.Current.Name -eq $openNavigationName) {
            $paneToggle.GetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 150
        }
        $navigationItem = Find-NavigationItem -Root $settingsWindow -Name $entry.Key
        if ($null -eq $navigationItem) {
            $available = @(
                $settingsWindow.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition) |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Current.Name) } |
                    Select-Object -First 80 |
                    ForEach-Object {
                        "$($_.Current.ControlType.ProgrammaticName):$($_.Current.Name):$($_.Current.AutomationId)"
                    }
            )
            throw "Settings navigation item '$($entry.Key)' was not found at minimum size. Available: $($available -join ' | ')"
        }
        $selection = $navigationItem.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        [void](Wait-Until -FailureMessage "Settings page '$($entry.Key)' did not become selected." -Condition {
            if ($selection.Current.IsSelected) { return $true }
            return $null
        })
        $title = Wait-Until -FailureMessage "Settings page title '$($entry.Value)' was not visible." -Condition {
            $candidate = Find-DescendantByName `
                -Root $settingsWindow `
                -Name $entry.Value `
                -ControlType ([System.Windows.Automation.ControlType]::Text)
            if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
                return $candidate
            }
            return $null
        }
        $visitedPages += $entry.Key

        if ($entry.Key -eq $dictionaryPageName) {
            $addDictionaryButton = Find-DescendantByName `
                -Root $settingsWindow `
                -Name $addDictionaryButtonName `
                -ControlType ([System.Windows.Automation.ControlType]::Button)
            if ($null -eq $addDictionaryButton -or $addDictionaryButton.Current.IsOffscreen) {
                throw 'The primary Add MDX action was not visible on the dictionary settings page.'
            }

            $dictionaryActionsFound = $true
            $dictionaryLibraryPanel = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'DictionaryLibraryPanelControl'
            $dictionaryEditorPanel = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'DictionaryEditorPanelControl'
            if ($null -eq $dictionaryLibraryPanel -or $null -eq $dictionaryEditorPanel) {
                throw 'The dictionary library and editor panels were not both exposed.'
            }
            $libraryBounds = $dictionaryLibraryPanel.Current.BoundingRectangle
            $editorBounds = $dictionaryEditorPanel.Current.BoundingRectangle
            if ($SettingsWidthDip -ge 820) {
                if ($editorBounds.Left -lt ($libraryBounds.Right + 8)) {
                    throw 'Dictionary panels did not use distinct side-by-side surfaces.'
                }
            }
            elseif ($editorBounds.Top -lt ($libraryBounds.Bottom + 8)) {
                throw 'Dictionary panels did not stack as distinct surfaces in narrow layout.'
            }
            $dictionaryPanelsDistinct = $true

            $dictionaryList = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'DictionaryListControl'
            $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            $dictionaryListItems = @(
                $dictionaryList.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $listItemCondition)
            )
            if ($dictionaryListItems.Count -ge 2) {
                $initialRuntimeIds = @(
                    $dictionaryListItems |
                        Select-Object -First 2 |
                        ForEach-Object { $_.GetRuntimeId() -join '.' }
                )

                $secondDictionarySelection = $dictionaryListItems[1].GetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern)
                $secondDictionarySelection.Select()
                [void](Wait-Until -FailureMessage 'The second dictionary could not be selected.' -Condition {
                    if ($secondDictionarySelection.Current.IsSelected) { return $true }
                    return $null
                })
                $dictionaryListItemsAfterSelection = @(
                    $dictionaryList.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        $listItemCondition)
                )
                for ($index = 0; $index -lt 2; $index++) {
                    if ($initialRuntimeIds[$index] -ne
                        ($dictionaryListItemsAfterSelection[$index].GetRuntimeId() -join '.')) {
                        throw 'Dictionary list item containers were recreated during selection.'
                    }
                }

                $firstDictionarySelection =
                    $dictionaryListItemsAfterSelection[0].GetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern)
                $firstDictionarySelection.Select()
                $dictionarySelectionContainersStable = $true
            }

            $advancedOptions = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'DictionaryAdvancedOptions'
            if ($null -eq $advancedOptions) {
                throw 'Advanced dictionary options were not exposed.'
            }
            $advancedExpandCollapse = $advancedOptions.GetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($advancedExpandCollapse.Current.ExpandCollapseState -ne
                [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                $advancedExpandCollapse.Expand()
                Start-Sleep -Milliseconds 150
            }
            [void](Test-ComboBoxFirstPointerClick `
                -Root $settingsWindow `
                -AutomationId 'DictionaryDarkModeSelectorControl' `
                -WindowHandle $settingsHwnd)
            $comboBoxesValidatedByFirstPointerClick +=
                'DictionaryDarkModeSelectorControl'
            $advancedExpandCollapse.Collapse()
            $settingsScroller = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'SettingsScrollViewer'
            $settingsScrollPattern = $null
            if ($null -ne $settingsScroller -and
                $settingsScroller.TryGetCurrentPattern(
                    [System.Windows.Automation.ScrollPattern]::Pattern,
                    [ref]$settingsScrollPattern) -and
                $settingsScrollPattern.Current.VerticallyScrollable) {
                $settingsScrollPattern.SetScrollPercent(
                    [System.Windows.Automation.ScrollPattern]::NoScroll,
                    0)
                Start-Sleep -Milliseconds 150
            }

            [VDictSmokeNative]::BringWindowToTop($settingsHwnd) | Out-Null
            [VDictSmokeNative]::SetForegroundWindow($settingsHwnd) | Out-Null
            Start-Sleep -Milliseconds 100
            $dictionarySettingsScreenshot = Save-WindowScreenshot `
                -Window $settingsHwnd `
                -Path (Join-Path $ScreenshotDirectory (
                    "settings-dictionaries-${SettingsWidthDip}x${SettingsHeightDip}.png"))
        }
        elseif ($entry.Key -eq $readingPageName) {
            foreach ($automationId in @(
                    'LanguageSelectorControl',
                    'ThemeSelectorControl')) {
                [void](Test-ComboBoxFirstPointerClick `
                    -Root $settingsWindow `
                    -AutomationId $automationId `
                    -WindowHandle $settingsHwnd)
                $comboBoxesValidatedByFirstPointerClick += $automationId
            }

            $contentWidthControl = Find-DescendantByAutomationId `
                -Root $settingsWindow `
                -AutomationId 'ContentWidthBoxControl'
            if ($null -eq $contentWidthControl) {
                throw 'Reader content-width control was missing from the scrollable settings page.'
            }

            $contentWidthControlFound = $true
        }
        elseif ($entry.Key -eq $securityPageName) {
            [void](Test-ComboBoxFirstPointerClick `
                -Root $settingsWindow `
                -AutomationId 'ExternalLinkSelectorControl' `
                -WindowHandle $settingsHwnd)
            $comboBoxesValidatedByFirstPointerClick += 'ExternalLinkSelectorControl'
        }
    }

    $paneToggle = Find-DescendantByAutomationId `
        -Root $settingsWindow `
        -AutomationId 'TogglePaneButton'
    if ($null -ne $paneToggle -and $paneToggle.Current.Name -eq $openNavigationName) {
        $paneToggle.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 150
    }
    $aboutNavigationItem = Find-NavigationItem -Root $settingsWindow -Name $aboutPageName
    $aboutSelection = $aboutNavigationItem.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $aboutSelection.Select()
    [void](Wait-Until -FailureMessage 'About page was not visible for screenshot capture.' -Condition {
        $candidate = Find-DescendantByName `
            -Root $settingsWindow `
            -Name $aboutPageName `
            -ControlType ([System.Windows.Automation.ControlType]::Text)
        if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
            return $candidate
        }
        return $null
    })

    $saveButtonName = if ($ExpectedUiLanguage -eq 'en-US') {
        'Save settings'
    }
    else {
        -join ([char[]]@(20445, 23384, 35774, 32622))
    }
    $cancelButtonName = if ($ExpectedUiLanguage -eq 'en-US') {
        'Cancel and close Settings'
    }
    else {
        -join ([char[]]@(21462, 28040, 24182, 20851, 38381, 35774, 32622))
    }
    $saveButton = Find-DescendantByName `
        -Root $settingsWindow `
        -Name $saveButtonName `
        -ControlType ([System.Windows.Automation.ControlType]::Button)
    $cancelButton = Find-DescendantByName `
        -Root $settingsWindow `
        -Name $cancelButtonName `
        -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $saveButton -or $saveButton.Current.IsOffscreen -or
        $null -eq $cancelButton -or $cancelButton.Current.IsOffscreen) {
        throw "Save and Cancel were not both discoverable at ${SettingsWidthDip}x${SettingsHeightDip} DIP."
    }

    [VDictSmokeNative]::BringWindowToTop($settingsHwnd) | Out-Null
    [VDictSmokeNative]::SetForegroundWindow($settingsHwnd) | Out-Null
    Start-Sleep -Milliseconds 100
    $settingsScreenshot = Save-WindowScreenshot `
        -Window $settingsHwnd `
        -Path (Join-Path $ScreenshotDirectory (
            "settings-${SettingsWidthDip}x${SettingsHeightDip}.png"))

    $windowPattern = $settingsWindow.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
    [void](Wait-Until -FailureMessage 'MainWindow was not re-enabled after SettingsWindow closed.' -Condition {
        if ([VDictSmokeNative]::IsWindowEnabled($mainHwnd)) {
            return $true
        }

        return $null
    })
    if ($webViewHosting -eq 'XamlComposition') {
        [void](Wait-Until -FailureMessage 'WebView XAML host did not become visible after SettingsWindow closed.' -Condition {
            if (-not $browserSurface.Current.IsOffscreen) { return $true }
            return $null
        })
    }

    $dataRoot = Join-Path $portableRoot 'Data'
    $requiredDirectories = @(
        (Join-Path $dataRoot 'Settings'),
        (Join-Path $dataRoot 'Cache\Indexes'),
        (Join-Path $dataRoot 'Cache\Resources'),
        (Join-Path $dataRoot 'WebView2'),
        (Join-Path $dataRoot 'Logs'),
        (Join-Path $dataRoot 'Temp')
    )
    $missingDirectories = @(
        $requiredDirectories |
            Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) }
    )
    if ($missingDirectories.Count -gt 0) {
        throw "Portable data directories were not created: $($missingDirectories -join ', ')"
    }

    [pscustomobject]@{
        OperatingSystem = [System.Environment]::OSVersion.VersionString
        ProcessId = $process.Id
        MainWindowHwnd = $mainHwnd.ToInt64()
        MainDpiScale = $mainScale
        WebViewScaleRequested = $WebViewScale
        WindowPositionRequested = if ($WindowX -lt 100000 -and $WindowY -lt 100000) {
            "$WindowX,$WindowY"
        } else {
            ''
        }
        SettingsWindowHwnd = $settingsHwnd.ToInt64()
        OwnerMatched = $true
        MainDisabledWhileOpen = $true
        MainReenabledAfterClose = $true
        SettingsWindowCount = $ownedSettings.Count
        SettingsAboveOwner = $true
        WebViewHiddenDuringModal = $true
        WebViewHosting = $webViewHosting
        SettingsSizeDip = "${SettingsWidthDip}x${SettingsHeightDip}"
        SettingsPagesVisited = $visitedPages
        SettingsActionsDiscoverable = $true
        DictionaryActionsDiscoverable = $dictionaryActionsFound
        DictionarySelectionContainersStable = $dictionarySelectionContainersStable
        DictionaryPanelsDistinct = $dictionaryPanelsDistinct
        ComboBoxesOpenedByFirstPointerClick = $comboBoxesValidatedByFirstPointerClick
        ReaderContentWidthControlDiscoverable = $contentWidthControlFound
        MainWindowScreenshot = $mainScreenshot
        SecondQueryScreenshot = $secondQueryScreenshot
        SettingsWindowScreenshot = $settingsScreenshot
        DictionarySettingsScreenshot = $dictionarySettingsScreenshot
        PortableDataRoot = $dataRoot
        PortableDataDirectoriesCreated = $true
        QueryRendered = -not [string]::IsNullOrWhiteSpace($ExpectedReaderText)
        SecondQueryRendered = -not [string]::IsNullOrWhiteSpace($SecondQuery)
        BackNavigationVerified = $backNavigationVerified
        PostClickTextRendered =
            -not [string]::IsNullOrWhiteSpace($ExpectedReaderTextAfterClick)
        ReaderActionInvoked = $readerActionInvoked
        ReaderPointerClicked = $readerPointerClicked
        ReaderClickButton = $ReaderClickButton
        ReaderSurfaceBounds = if ($null -ne $readerSurfaceBounds) {
            $readerSurfaceBounds.ToString()
        } else {
            ''
        }
        ReaderClickScreenPosition = $readerClickScreenPosition
    }
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }

    $process.Dispose()
}
