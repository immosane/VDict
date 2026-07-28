[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PortableDirectory,

    [Parameter(Mandatory)]
    [string] $SourceSettingsPath,

    [Parameter()]
    [ValidateSet('system', 'light', 'dark')]
    [string] $Theme = 'system',

    [Parameter()]
    [ValidateRange(30, 100)]
    [int] $ContentWidthPercent = 80,

    [Parameter()]
    [string] $Query = 'can'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$portableRoot = (Resolve-Path -LiteralPath $PortableDirectory).Path
$sourcePath = (Resolve-Path -LiteralPath $SourceSettingsPath).Path
$source = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
$dictionary = $source.dictionaries |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.mdx_path) -and
        (Test-Path -LiteralPath $_.mdx_path -PathType Leaf)
    } |
    Select-Object -First 1
if ($null -eq $dictionary) {
    throw "No existing dictionary from '$sourcePath' can be used for the Oxford smoke test."
}

$mddPaths = @(
    $dictionary.mdd_paths |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
)
$cssPaths = @(
    $dictionary.css_paths |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
)
$sourceScriptPaths = if ($dictionary.PSObject.Properties.Name -contains 'script_paths') {
    $dictionary.script_paths
} else {
    @()
}
$scriptPaths = @(
    $sourceScriptPaths |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
)
$settings = [ordered]@{
    schema_version = 5
    revision = 1
    dictionaries = @(
        [ordered]@{
            id = 'phase6-oxford'
            name = [string]$dictionary.name
            display_name = 'Oxford Phase 6'
            mdx_path = [string]$dictionary.mdx_path
            enabled = $true
            block_network = $true
            local_script_mode = 'standard'
            mdd_paths = $mddPaths
            css_paths = $cssPaths
            script_paths = $scriptPaths
            font_size = 18
            line_height = 1.65
            latin_font_family = 'Segoe UI'
            cjk_font_family = 'Microsoft YaHei'
            override_font = $false
            override_typography = $false
            interaction_mode = 'auto'
            dark_mode_strategy = if ($Theme -eq 'dark') { 'dark' } else { 'original' }
        }
    )
    style = [ordered]@{
        font_size = 18
        line_height = 1.65
        content_zoom = 1.0
        content_width_percent = $ContentWidthPercent
        theme = $Theme
        app_font_family = 'Microsoft YaHei UI'
        latin_font_family = 'Segoe UI'
        cjk_font_family = 'Microsoft YaHei'
    }
    reduce_dictionary_brightness_in_dark_mode = $true
    external_link_policy = 'block'
    compatibility_diagnostics_enabled = $true
}

$settingsDirectory = Join-Path $portableRoot 'Data\Settings'
[System.IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$json = $settings | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    $settingsPath,
    $json,
    [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Dictionary = $dictionary.name
    MdxPath = $dictionary.mdx_path
    MddCount = $mddPaths.Count
    CssCount = $cssPaths.Count
    Theme = $Theme
    ContentWidthPercent = $ContentWidthPercent
    Query = $Query
    SettingsPath = $settingsPath
}
