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

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);

    public static void Key(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 0x0002, UIntPtr.Zero);
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
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

function Find-AutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
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

function Set-EditorValue {
    param(
        [System.Windows.Automation.AutomationElement]$Editor,
        [string]$Value,
        [IntPtr]$WindowHandle
    )

    $valueElement = $Editor
    if ($Editor.Current.ControlType -eq [System.Windows.Automation.ControlType]::Spinner) {
        $editCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)
        $innerEdit = $Editor.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $editCondition)
        if ($null -ne $innerEdit) {
            $valueElement = $innerEdit
        }
    }

    $null = [VDictSettingsSmokeNative]::SetForegroundWindow($WindowHandle)
    try {
        $valueElement.SetFocus()
    }
    catch {
        throw "Editor '$($Editor.Current.Name)' " +
            "('$($Editor.Current.AutomationId)', $($Editor.Current.ControlType.ProgrammaticName)) " +
            "cannot receive focus: $($_.Exception.Message)"
    }
    Wait-Until -Failure "Editor '$($Editor.Current.Name)' did not receive focus." -Condition {
        if ($valueElement.Current.HasKeyboardFocus) {
            return $true
        }

        return $null
    } | Out-Null
    $pattern = $valueElement.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
}

function Test-EditorHasKeyboardFocus {
    param([System.Windows.Automation.AutomationElement]$Editor)

    if ($Editor.Current.HasKeyboardFocus) {
        return $true
    }

    $focusedCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty,
        $true)
    return $null -ne $Editor.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $focusedCondition)
}

function Wait-SaveState {
    param(
        [System.Windows.Automation.AutomationElement]$SaveButton,
        [bool]$Enabled,
        [string]$Failure
    )

    Wait-Until -Failure $Failure -Condition {
        if ($SaveButton.Current.IsEnabled -eq $Enabled) {
            return $true
        }

        return $null
    } | Out-Null
}

function Press-Enter {
    param([IntPtr]$WindowHandle)

    $null = [VDictSettingsSmokeNative]::SetForegroundWindow($WindowHandle)
    [VDictSettingsSmokeNative]::Key(0x0D)
}

function Click-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [IntPtr]$WindowHandle
    )

    $null = [VDictSettingsSmokeNative]::SetForegroundWindow($WindowHandle)
    $scrollPattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern,
            [ref]$scrollPattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$scrollPattern).ScrollIntoView()
        Start-Sleep -Milliseconds 100
    }
    $bounds = $Element.Current.BoundingRectangle
    if ([double]::IsNaN($bounds.Left) -or
        [double]::IsNaN($bounds.Top) -or
        $bounds.Width -le 0 -or
        $bounds.Height -le 0) {
        throw "Element '$($Element.Current.Name)' has no clickable bounds."
    }
    [VDictSettingsSmokeNative]::Click(
        [int]($bounds.Left + $bounds.Width / 2),
        [int]($bounds.Top + $bounds.Height / 2))
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

$saveButton = Find-NamedElement -Root $settingsWindow -Name '保存设置'
$cancelButton = Find-NamedElement -Root $settingsWindow -Name '取消并关闭设置'
if ($null -eq $saveButton -or $null -eq $cancelButton) {
    throw 'The Settings footer actions were not found.'
}

Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $false `
    -Failure 'Settings unexpectedly started with a dirty draft.'

$dictionaryInputChecks = 'skipped-no-dictionary'
$dictionaryDisplayName = Find-NamedControl `
    -Root $settingsWindow `
    -Name '显示名称（留空使用词典名称）' `
    -ControlType ([System.Windows.Automation.ControlType]::Edit)
if ($null -ne $dictionaryDisplayName) {
    $dictionaryInputChecks = 'passed'
    $displayNamePattern = $dictionaryDisplayName.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $originalDisplayName = ([System.Windows.Automation.ValuePattern]$displayNamePattern).Current.Value
    Set-EditorValue `
        -Editor $dictionaryDisplayName `
        -Value ($originalDisplayName + '-smoke') `
        -WindowHandle $handle
    Wait-SaveState `
        -SaveButton $saveButton `
        -Enabled $true `
        -Failure 'Editing the dictionary display name did not immediately enable Save.'
    if (-not $dictionaryDisplayName.Current.HasKeyboardFocus) {
        throw 'The dictionary display name lost focus during immediate change detection.'
    }
    Set-EditorValue `
        -Editor $dictionaryDisplayName `
        -Value $originalDisplayName `
        -WindowHandle $handle
    Wait-SaveState `
        -SaveButton $saveButton `
        -Enabled $false `
        -Failure 'Reverting the dictionary display name did not immediately clear the draft.'

    $advancedOptions = Find-NamedElement -Root $settingsWindow -Name '高级选项'
    if ($null -eq $advancedOptions) {
        throw 'Dictionary advanced options were not found.'
    }
    Click-Element -Element $advancedOptions -WindowHandle $handle

    foreach ($fontName in @('拉丁字体', '中日韩字体')) {
        $fontEditor = Wait-Until `
            -Failure "Dictionary font editor '$fontName' did not become available." `
            -Condition {
                $candidate = Find-NamedControl `
                    -Root $settingsWindow `
                    -Name $fontName `
                    -ControlType ([System.Windows.Automation.ControlType]::Edit)
                if ($null -ne $candidate) {
                    return $candidate
                }

                return $null
            }
        $fontPattern = $fontEditor.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $originalFont = ([System.Windows.Automation.ValuePattern]$fontPattern).Current.Value
        Set-EditorValue `
            -Editor $fontEditor `
            -Value ($originalFont + '-smoke') `
            -WindowHandle $handle
        Wait-SaveState `
            -SaveButton $saveButton `
            -Enabled $true `
            -Failure "Editing dictionary font '$fontName' did not immediately enable Save."
        Set-EditorValue `
            -Editor $fontEditor `
            -Value $originalFont `
            -WindowHandle $handle
        Wait-SaveState `
            -SaveButton $saveButton `
            -Enabled $false `
            -Failure "Reverting dictionary font '$fontName' did not immediately clear the draft."
    }

}

Select-NavigationItem -Window $settingsWindow -Name '阅读与外观'
$readingHeading = Wait-Until -Failure 'The Reading heading did not become visible.' -Condition {
    $candidate = Find-NamedControl `
        -Root $settingsWindow `
        -Name '阅读与外观' `
        -ControlType ([System.Windows.Automation.ControlType]::Text)
    if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
        return $candidate
    }

    return $null
}
$latinFont = Wait-Until -Failure 'The Reading Latin font editor was not found.' -Condition {
    $candidate = Find-NamedControl `
        -Root $settingsWindow `
        -Name '正文拉丁字体' `
        -ControlType ([System.Windows.Automation.ControlType]::Edit)
    if ($null -ne $candidate -and -not $candidate.Current.IsOffscreen) {
        return $candidate
    }

    return $null
}
$latinPattern = $latinFont.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern)
$originalLatinFont = ([System.Windows.Automation.ValuePattern]$latinPattern).Current.Value
Set-EditorValue `
    -Editor $latinFont `
    -Value ($originalLatinFont + '-enter-smoke') `
    -WindowHandle $handle
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'Editing the Reading Latin font did not immediately enable Save.'
if (-not $latinFont.Current.HasKeyboardFocus) {
    throw 'The Reading Latin font lost focus during immediate change detection.'
}
Press-Enter -WindowHandle $handle
Wait-Until -Failure 'The Reading Latin font retained focus after Enter.' -Condition {
    if (-not $latinFont.Current.HasKeyboardFocus) {
        return $true
    }

    return $null
} | Out-Null
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'The Reading Latin font change was lost after Enter.'

Set-EditorValue `
    -Editor $latinFont `
    -Value ($originalLatinFont + '-blank-smoke') `
    -WindowHandle $handle
Click-Element -Element $readingHeading -WindowHandle $handle
Wait-Until -Failure 'The Reading Latin font retained focus after a blank-area click.' -Condition {
    if (-not $latinFont.Current.HasKeyboardFocus) {
        return $true
    }

    return $null
} | Out-Null
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'The Reading Latin font change was lost after a blank-area click.'

Set-EditorValue `
    -Editor $latinFont `
    -Value $originalLatinFont `
    -WindowHandle $handle
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $false `
    -Failure 'Reverting the Reading Latin font did not immediately clear the draft.'

$cjkFont = Find-NamedControl `
    -Root $settingsWindow `
    -Name '正文中日韩字体' `
    -ControlType ([System.Windows.Automation.ControlType]::Edit)
if ($null -eq $cjkFont) {
    throw 'The Reading CJK font editor was not found.'
}
$cjkPattern = $cjkFont.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern)
$originalCjkFont = ([System.Windows.Automation.ValuePattern]$cjkPattern).Current.Value
Set-EditorValue `
    -Editor $cjkFont `
    -Value ($originalCjkFont + '-smoke') `
    -WindowHandle $handle
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'Editing the Reading CJK font did not immediately enable Save.'
Set-EditorValue `
    -Editor $cjkFont `
    -Value $originalCjkFont `
    -WindowHandle $handle
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $false `
    -Failure 'Reverting the Reading CJK font did not immediately clear the draft.'

$appFontSize = Find-AutomationId `
    -Root $settingsWindow `
    -AutomationId 'AppFontSizeBoxControl'
if ($null -eq $appFontSize) {
    throw 'The Reading NumberBox was not found.'
}
Set-EditorValue `
    -Editor $appFontSize `
    -Value '47' `
    -WindowHandle $handle
Press-Enter -WindowHandle $handle
Wait-Until -Failure 'The Reading NumberBox retained focus after Enter.' -Condition {
    if (-not (Test-EditorHasKeyboardFocus -Editor $appFontSize)) {
        return $true
    }

    return $null
} | Out-Null
if (-not $saveButton.Current.IsEnabled) {
    Set-EditorValue `
        -Editor $appFontSize `
        -Value '46' `
        -WindowHandle $handle
    Press-Enter -WindowHandle $handle
    Wait-Until -Failure 'The Reading NumberBox retained focus after its second Enter.' -Condition {
        if (-not (Test-EditorHasKeyboardFocus -Editor $appFontSize)) {
            return $true
        }

        return $null
    } | Out-Null
}
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'The Reading NumberBox did not commit an Enter value.'

Set-EditorValue `
    -Editor $appFontSize `
    -Value '45' `
    -WindowHandle $handle
Click-Element -Element $readingHeading -WindowHandle $handle
Wait-Until -Failure 'The Reading NumberBox retained focus after a blank-area click.' -Condition {
    if (-not (Test-EditorHasKeyboardFocus -Editor $appFontSize)) {
        return $true
    }

    return $null
} | Out-Null
Wait-SaveState `
    -SaveButton $saveButton `
    -Enabled $true `
    -Failure 'The Reading NumberBox did not retain its blank-click value as pending.'

if ($dictionaryInputChecks -eq 'passed') {
    Select-NavigationItem -Window $settingsWindow -Name '词库'
    $dictionaryFontSize = Wait-Until `
        -Failure 'Dictionary NumberBox did not become available.' `
        -Condition {
            $candidate = Find-AutomationId `
                -Root $settingsWindow `
                -AutomationId 'DictionaryFontSizeBoxControl'
            if ($null -ne $candidate) {
                return $candidate
            }

            return $null
        }
    $dictionaryAppearanceHeading = Find-NamedControl `
        -Root $settingsWindow `
        -Name '正文外观' `
        -ControlType ([System.Windows.Automation.ControlType]::Text)
    if ($null -eq $dictionaryAppearanceHeading) {
        throw 'Dictionary NumberBox blank-click target was not found.'
    }
    Set-EditorValue `
        -Editor $dictionaryFontSize `
        -Value '18' `
        -WindowHandle $handle
    Press-Enter -WindowHandle $handle
    Wait-Until -Failure 'Dictionary NumberBox retained focus after Enter.' -Condition {
        if (-not (Test-EditorHasKeyboardFocus -Editor $dictionaryFontSize)) {
            return $true
        }

        return $null
    } | Out-Null
    Set-EditorValue `
        -Editor $dictionaryFontSize `
        -Value '19' `
        -WindowHandle $handle
    Click-Element -Element $dictionaryAppearanceHeading -WindowHandle $handle
    Wait-Until -Failure 'Dictionary NumberBox retained focus after a blank-area click.' -Condition {
        if (-not (Test-EditorHasKeyboardFocus -Editor $dictionaryFontSize)) {
            return $true
        }

        return $null
    } | Out-Null
    Wait-SaveState `
        -SaveButton $saveButton `
        -Enabled $true `
        -Failure 'Dictionary NumberBox did not retain its pending value.'
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
    schema = 'settings-phase4-smoke-v2'
    process_id = $ProcessId
    navigation = $expectedNavigation
    visit_sequence = $visited
    retired_global_item_absent = $true
    input_behavior = @{
        dictionary_fields = $dictionaryInputChecks
        reading_fonts_immediate = $true
        enter_clears_focus = $true
        blank_click_clears_focus = $true
        number_boxes_commit = $true
    }
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
