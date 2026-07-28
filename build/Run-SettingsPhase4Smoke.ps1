param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VDictSettingsSmokeNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$Failure,
        [int]$TimeoutMilliseconds = 10000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $value = & $Condition
        if ($null -ne $value) {
            return $value
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw $Failure
}

function Find-NamedElement {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Find-NamedControl {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)))
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Select-NavigationItem {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    $item = Find-NamedElement -Root $Window -Name $Name
    if ($null -eq $item) {
        throw "Navigation item '$Name' was not found."
    }

    $pattern = $null
    if ($item.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }

    if ($item.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }

    throw "Navigation item '$Name' exposes neither SelectionItem nor Invoke."
}

$process = Get-Process -Id $ProcessId
$processCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $ProcessId)
$mainWindow = Wait-Until -Failure 'VDict main window did not become ready.' -Condition {
    $process.Refresh()
    if ($process.HasExited) {
        throw "VDict exited early with code $($process.ExitCode)."
    }

    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    foreach ($window in $windows) {
        if ($window.Current.Name -in @('VDict', 'VDict.exe')) {
            return $window
        }
    }

    return $null
}

$settingsButton = Wait-Until -Failure 'The Settings button was not found.' -Condition {
    $candidate = Find-NamedControl `
        -Root $mainWindow `
        -Name '设置' `
        -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -ne $candidate -and $candidate.Current.IsEnabled) {
        return $candidate
    }

    return $null
}
$invokePattern = $settingsButton.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
([System.Windows.Automation.InvokePattern]$invokePattern).Invoke()

$settingsWindow = Wait-Until -Failure 'The Settings window did not open.' -Condition {
    $windowCondition = New-Object System.Windows.Automation.AndCondition(
        $processCondition,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $windowCondition)
    foreach ($window in $windows) {
        if ($window.Current.ProcessId -eq $ProcessId -and
            $window.Current.NativeWindowHandle -ne
                $mainWindow.Current.NativeWindowHandle) {
            return $window
        }
    }

    return $null
}

$expectedNavigation = @(
    '词库',
    '阅读与外观',
    '安全与兼容',
    '数据与维护',
    '关于'
)
foreach ($name in $expectedNavigation) {
    if ($null -eq (Find-NamedElement -Root $settingsWindow -Name $name)) {
        throw "Expected navigation item '$name' was not found."
    }
}
if ($null -ne (Find-NamedElement -Root $settingsWindow -Name '全局设置')) {
    throw 'The retired Global Settings navigation item is still present.'
}

$visited = [System.Collections.Generic.List[string]]::new()
foreach ($name in @('阅读与外观', '安全与兼容', '数据与维护', '词库', '关于', '词库')) {
    Select-NavigationItem -Window $settingsWindow -Name $name
    $heading = Wait-Until -Failure "Section '$name' did not become visible." -Condition {
        $candidate = Find-NamedElement -Root $settingsWindow -Name $name
        if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
            return $candidate
        }

        return $null
    }
    $visited.Add($heading.Current.Name)
}

$handle = [IntPtr]$settingsWindow.Current.NativeWindowHandle
if ($handle -eq [IntPtr]::Zero) {
    throw 'The Settings window does not expose a native window handle.'
}

$null = [VDictSettingsSmokeNative]::SetWindowPos(
    $handle,
    [IntPtr]::Zero,
    120,
    120,
    760,
    520,
    0x0014)
Start-Sleep -Milliseconds 350
$narrowBounds = $settingsWindow.Current.BoundingRectangle
$saveButton = Find-NamedElement -Root $settingsWindow -Name '保存设置'
$cancelButton = Find-NamedElement -Root $settingsWindow -Name '取消并关闭设置'
foreach ($button in @($saveButton, $cancelButton)) {
    if ($null -eq $button -or $button.Current.IsOffscreen) {
        throw 'A footer action became unavailable in the narrow layout.'
    }

    $bounds = $button.Current.BoundingRectangle
    if ($bounds.Left -lt $narrowBounds.Left -or
        $bounds.Right -gt $narrowBounds.Right -or
        $bounds.Bottom -gt $narrowBounds.Bottom) {
        throw 'A footer action overflowed the narrow Settings window.'
    }
}

$cancelPattern = $cancelButton.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
([System.Windows.Automation.InvokePattern]$cancelPattern).Invoke()
Wait-Until -Failure 'The Settings window did not close after Cancel.' -Condition {
    try {
        if ($settingsWindow.Current.IsOffscreen) {
            return $true
        }
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
        return $true
    }

    return $null
} | Out-Null

$result = [ordered]@{
    schema = 'settings-phase4-smoke-v1'
    process_id = $ProcessId
    navigation = $expectedNavigation
    visit_sequence = $visited
    retired_global_item_absent = $true
    narrow_window = @{
        width = [Math]::Round($narrowBounds.Width, 2)
        height = [Math]::Round($narrowBounds.Height, 2)
        footer_actions_visible = $true
    }
    cancel_closed_window = $true
}
$json = $result | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    $directory = [IO.Path]::GetDirectoryName($fullOutputPath)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    [IO.File]::WriteAllText($fullOutputPath, $json)
}

$json
