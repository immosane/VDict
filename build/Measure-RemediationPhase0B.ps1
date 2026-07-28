[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 100)]
    [int] $Samples = 30,

    [Parameter()]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\remediation-0b'),

    [Parameter()]
    [ValidateRange(30, 1800)]
    [int] $TimeoutSeconds = 900,

    [Parameter()]
    [string] $CanaryManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-WithinDirectory {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith(
        $prefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the approved baseline directory: $fullPath"
    }
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)][double[]] $Values,
        [Parameter(Mandatory)][ValidateRange(0, 1)][double] $Percentile
    )

    if ($Values.Count -eq 0) {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling($Percentile * $sorted.Count) - 1
    return [double]$sorted[[Math]::Max(0, $index)]
}

function Get-MetricSummary {
    param(
        [Parameter(Mandatory)][object[]] $Samples,
        [Parameter(Mandatory)][string] $Property
    )

    [double[]]$values = @($Samples | ForEach-Object { [double]$_.$Property })
    return [ordered]@{
        n = $values.Count
        p50 = [Math]::Round((Get-Percentile -Values $values -Percentile 0.50), 3)
        p95 = [Math]::Round((Get-Percentile -Values $values -Percentile 0.95), 3)
        max = [Math]::Round((($values | Measure-Object -Maximum).Maximum), 3)
    }
}

function Get-TimelineStageSummaries {
    param(
        [Parameter(Mandatory)][string] $LogDirectory
    )

    $valuesByStage = @{}
    if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        return [ordered]@{}
    }

    foreach ($logFile in Get-ChildItem -LiteralPath $LogDirectory -Filter 'session-*.log') {
        foreach ($line in Get-Content -LiteralPath $logFile.FullName -Encoding UTF8) {
            if ($line -notmatch 'Schema=reader-timeline-v1;') {
                continue
            }
            if ($line -notmatch
                'Stage=(?<stage>[^;]+);.*StageBuildMs=(?<milliseconds>[0-9.]+);') {
                continue
            }

            $stage = $Matches['stage'].Split(':')[0]
            if (-not $valuesByStage.ContainsKey($stage)) {
                $valuesByStage[$stage] = [System.Collections.Generic.List[double]]::new()
            }
            $valuesByStage[$stage].Add(
                [double]::Parse(
                    $Matches['milliseconds'],
                    [System.Globalization.CultureInfo]::InvariantCulture))
        }
    }

    $result = [ordered]@{}
    foreach ($stage in @($valuesByStage.Keys | Sort-Object)) {
        $stageSamples = @(
            $valuesByStage[$stage] |
                ForEach-Object { [pscustomobject]@{ Value = $_ } }
        )
        $result[$stage] = Get-MetricSummary `
            -Samples $stageSamples `
            -Property Value
    }
    return $result
}

function Get-AudioResourceObservations {
    param(
        [Parameter(Mandatory)][string] $LogDirectory
    )

    $results = @()
    if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        return $results
    }

    foreach ($logFile in Get-ChildItem -LiteralPath $LogDirectory -Filter 'session-*.log') {
        foreach ($line in Get-Content -LiteralPath $logFile.FullName -Encoding UTF8) {
            if ($line -notmatch '\[compatibility\].*ContentType=audio/') {
                continue
            }
            $fields = [ordered]@{}
            foreach ($name in @(
                'Render', 'Method', 'Path', 'LookupKey', 'Source', 'CacheHit',
                'Range', 'MddInitMs', 'PersistentIndexHit', 'Status', 'ContentType',
                'FullLength', 'ReturnedBytes', 'ContentRange', 'SourceBytesRead',
                'RecordBlockBytesDecoded', 'ResourceBytesMaterialized',
                'ResourceArrayCopies', 'DecodedResourceCacheHit',
                'RecordBlockCacheHit', 'ResponseStreamCopiedBytes',
                'ResponseStreamCopyCount', 'ManagedHeapPeakDelta',
                'WorkingSetPeakDelta', 'ResolveElapsedMs', 'ElapsedMs')) {
                if ($line -match "(?:^|; )$name=(?<value>[^;]*)(?:;|$)") {
                    $fields[$name] = $Matches['value']
                }
            }
            $results += [pscustomobject]$fields
        }
    }
    return $results
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$workRoot = Join-Path $outputRoot "work-$runId"
$portableRoot = Join-Path $workRoot 'portable'
$resultRoot = Join-Path $outputRoot "result-$runId"
$fixtureRoot = Join-Path $repositoryRoot 'artifacts\remediation-fixture'
$rawReport = Join-Path $portableRoot 'Data\Logs\remediation-baseline.json'
$failureReport = "$rawReport.failure.txt"
Assert-WithinDirectory -Path $workRoot -Parent $outputRoot
Assert-WithinDirectory -Path $resultRoot -Parent $outputRoot
New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $workRoot '.vdict-remediation-work') `
    -Value 'remediation-0b' `
    -Encoding UTF8

try {
    & (Join-Path $PSScriptRoot 'Run-RemediationPhase0A.ps1') `
        -OutputDirectory $fixtureRoot `
        -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Phase 0A fixture preparation failed with exit code $LASTEXITCODE."
    }

    dotnet publish `
        (Join-Path $repositoryRoot 'src\VDict.WinUI\VDict.WinUI.csproj') `
        --configuration Portable `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $portableRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Portable publish failed with exit code $LASTEXITCODE."
    }

    $settingsDirectory = Join-Path $portableRoot 'Data\Settings'
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    $settings = [ordered]@{
        schema_version = 5
        revision = 1
        dictionaries = @(
            [ordered]@{
                id = 'remediation-fixture'
                name = 'VDict Remediation Fixture'
                display_name = 'VDict Remediation Fixture'
                mdx_path = (Join-Path $fixtureRoot 'vdict-remediation.mdx')
                enabled = $true
                block_network = $true
                local_script_mode = 'standard'
                mdd_paths = @((Join-Path $fixtureRoot 'vdict-remediation.mdd'))
                css_paths = @((Join-Path $fixtureRoot 'styles\fixture.css'))
                script_paths = @((Join-Path $fixtureRoot 'scripts\standard.js'))
                font_size = 18
                line_height = 1.65
                latin_font_family = 'Segoe UI'
                cjk_font_family = 'Microsoft YaHei'
                override_font = $false
                override_typography = $false
                interaction_mode = 'auto'
                dark_mode_strategy = 'global'
            }
        )
        style = [ordered]@{
            font_size = 18
            line_height = 1.65
            content_zoom = 1.0
            content_width_percent = 80
            theme = 'system'
            app_font_family = 'Microsoft YaHei UI'
            latin_font_family = 'Segoe UI'
            cjk_font_family = 'Microsoft YaHei'
        }
        reduce_dictionary_brightness_in_dark_mode = $true
        external_link_policy = 'block'
        compatibility_diagnostics_enabled = $true
    }
    $settings |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $settingsDirectory 'settings.json') `
            -Encoding UTF8

    $executable = Join-Path $portableRoot 'VDict.exe'
    $process = Start-Process `
        -FilePath $executable `
        -WorkingDirectory $portableRoot `
        -ArgumentList @(
            '--remediation-baseline-output', $rawReport,
            '--remediation-baseline-samples', $Samples
        ) `
        -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }

    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            $process.WaitForExit()
        }
        throw "Baseline host timed out after $TimeoutSeconds seconds."
    }

    if (Test-Path -LiteralPath $failureReport -PathType Leaf) {
        throw "Baseline host failed: $(Get-Content -LiteralPath $failureReport -Raw)"
    }
    if (-not (Test-Path -LiteralPath $rawReport -PathType Leaf)) {
        throw "Baseline host exited without writing $rawReport."
    }

    $report = Get-Content -LiteralPath $rawReport -Encoding UTF8 -Raw |
        ConvertFrom-Json
    $cold = @($report.QuerySamples | Where-Object Scenario -eq 'perf-small-cold')
    $small = @($report.QuerySamples | Where-Object Scenario -eq 'perf-small-warm')
    $large = @($report.QuerySamples | Where-Object Scenario -eq 'perf-large-warm')
    $lightTheme = @($report.Themes.Samples | Where-Object Theme -eq 'Light')
    $darkTheme = @($report.Themes.Samples | Where-Object Theme -eq 'Dark')
    $systemTheme = @($report.Themes.Samples | Where-Object Theme -eq 'System')
    $logs = Join-Path $portableRoot 'Data\Logs'
    $timelineStages = Get-TimelineStageSummaries -LogDirectory $logs
    $audioResourceObservations = Get-AudioResourceObservations -LogDirectory $logs
    $canaries = @()
    $canaryResults = @()
    $requiredCanaryCapabilities =
        @('scripted', 'complex-css', 'mdd-audio', 'image-viewer', 'multi-entry')
    if (-not [string]::IsNullOrWhiteSpace($CanaryManifest)) {
        $canaryPath = (Resolve-Path -LiteralPath $CanaryManifest).Path
        $canaryDocument = Get-Content -LiteralPath $canaryPath -Encoding UTF8 -Raw |
            ConvertFrom-Json
        $canaries = @($canaryDocument.dictionaries)
        if ($canaries.Count -lt 2) {
            throw 'The real dictionary canary manifest must describe at least two dictionaries.'
        }
        foreach ($canary in $canaries) {
            if (-not (Test-Path -LiteralPath $canary.mdx_path -PathType Leaf)) {
                throw "Canary MDX was not found: $($canary.mdx_path)"
            }
        }

        $canaryIndex = 0
        foreach ($canary in $canaries) {
            $canaryIndex++
            $queries = @($canary.queries)
            if ($queries.Count -eq 0 -or
                [string]::IsNullOrWhiteSpace([string]$queries[0])) {
                throw "Canary '$($canary.name)' must provide at least one query."
            }
            $mddPaths = if ($canary.PSObject.Properties.Name -contains 'mdd_paths') {
                @($canary.mdd_paths)
            } else {
                @()
            }
            $cssPaths = if ($canary.PSObject.Properties.Name -contains 'css_paths') {
                @($canary.css_paths)
            } else {
                @()
            }
            $scriptPaths = if ($canary.PSObject.Properties.Name -contains 'script_paths') {
                @($canary.script_paths)
            } else {
                @()
            }
            $scriptMode = if (
                $canary.PSObject.Properties.Name -contains 'local_script_mode') {
                [string]$canary.local_script_mode
            } elseif (@($canary.capabilities) -contains 'scripted') {
                'standard'
            } else {
                'off'
            }
            $settings.revision = [long]$settings.revision + 1
            $settings.dictionaries = @(
                [ordered]@{
                    id = "remediation-canary-$canaryIndex"
                    name = [string]$canary.name
                    display_name = [string]$canary.name
                    mdx_path = [string]$canary.mdx_path
                    enabled = $true
                    block_network = $true
                    local_script_mode = $scriptMode
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
                    dark_mode_strategy = 'original'
                }
            )
            $settings |
                ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath (
                    Join-Path $settingsDirectory 'settings.json') -Encoding UTF8
            $canaryScreenshotDirectory = Join-Path `
                $resultRoot "canaries\canary-$canaryIndex"
            $smokeResult = & (Join-Path $PSScriptRoot 'RuntimeSmoke.ps1') `
                -PortableDirectory $portableRoot `
                -Query ([string]$queries[0]) `
                -ExpectedReaderText ([string]$queries[0]) `
                -ScreenshotDirectory $canaryScreenshotDirectory `
                -TimeoutSeconds ([Math]::Min(120, $TimeoutSeconds)) `
                -SkipSettingsValidation
            $canaryResults += [ordered]@{
                name = [string]$canary.name
                capabilities = @($canary.capabilities)
                query = [string]$queries[0]
                passed = [bool]$smokeResult.QueryRendered
                screenshot = "canaries/canary-$canaryIndex/main-window.png"
            }
        }
    }
    $coveredCanaryCapabilities = @(
        $canaryResults |
            ForEach-Object { @($_.capabilities) } |
            Sort-Object -Unique
    )
    $missingCanaryCapabilities = @(
        $requiredCanaryCapabilities |
            Where-Object { $_ -notin $coveredCanaryCapabilities }
    )

    $summary = [ordered]@{
        schema = 'reader-baseline-summary-v1'
        run_id = $runId
        generated_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
        generator_version = '0a.1'
        fixture_semantic_sha256 =
            '0856edfd57fbc009b1c4ef9f66ee182d5ed0be2e83d13f40edb1f2b6b73407ca'
        environment = $report.Environment
        definitions = [ordered]@{
            cold = 'new portable process; isolated settings/WebView2 data; catalog initialized; first content navigation'
            warm = 'same process, catalog, stores, WebView2 profile and fixture; no cache reset between samples'
            outliers = 'none removed'
            percentile = 'nearest-rank'
            requested_warm_samples_per_scenario = $Samples
        }
        metrics = [ordered]@{
            cold_small_end_to_end_ms =
                Get-MetricSummary -Samples $cold -Property EndToEndMilliseconds
            cold_small_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $cold `
                    -Property NavigationToFirstFrameReadyMilliseconds
            cold_small_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $cold `
                    -Property NavigationToAllFramesReadyMilliseconds
            small_end_to_end_ms =
                Get-MetricSummary -Samples $small -Property EndToEndMilliseconds
            small_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $small -Property NavigationToAllFramesReadyMilliseconds
            small_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $small -Property NavigationToFirstFrameReadyMilliseconds
            small_entry_rewrite_ms =
                Get-MetricSummary -Samples $small -Property EntryRewriteMilliseconds
            small_document_build_ms =
                Get-MetricSummary -Samples $small -Property DocumentBuildMilliseconds
            large_end_to_end_ms =
                Get-MetricSummary -Samples $large -Property EndToEndMilliseconds
            large_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $large -Property NavigationToAllFramesReadyMilliseconds
            large_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $large -Property NavigationToFirstFrameReadyMilliseconds
            large_entry_rewrite_ms =
                Get-MetricSummary -Samples $large -Property EntryRewriteMilliseconds
            large_document_build_ms =
                Get-MetricSummary -Samples $large -Property DocumentBuildMilliseconds
            light_theme_end_to_end_ms =
                Get-MetricSummary -Samples $lightTheme -Property EndToEndMilliseconds
            dark_theme_end_to_end_ms =
                Get-MetricSummary -Samples $darkTheme -Property EndToEndMilliseconds
            system_theme_end_to_end_ms =
                Get-MetricSummary -Samples $systemTheme -Property EndToEndMilliseconds
            light_dynamic_theme_settle_ms =
                Get-MetricSummary -Samples $lightTheme -Property DynamicThemeSettleMilliseconds
            dark_dynamic_theme_settle_ms =
                Get-MetricSummary -Samples $darkTheme -Property DynamicThemeSettleMilliseconds
            system_dynamic_theme_settle_ms =
                Get-MetricSummary -Samples $systemTheme -Property DynamicThemeSettleMilliseconds
            light_theme_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $lightTheme `
                    -Property NavigationToAllFramesReadyMilliseconds
            dark_theme_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $darkTheme `
                    -Property NavigationToAllFramesReadyMilliseconds
            system_theme_navigation_to_all_frames_ready_ms =
                Get-MetricSummary -Samples $systemTheme `
                    -Property NavigationToAllFramesReadyMilliseconds
            light_theme_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $lightTheme `
                    -Property NavigationToFirstFrameReadyMilliseconds
            dark_theme_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $darkTheme `
                    -Property NavigationToFirstFrameReadyMilliseconds
            system_theme_navigation_to_first_frame_ready_ms =
                Get-MetricSummary -Samples $systemTheme `
                    -Property NavigationToFirstFrameReadyMilliseconds
            render_timeline_stage_build_ms = $timelineStages
        }
        resources = $report.Resources
        zoom = $report.Zoom
        settings_concurrency = $report.SettingsConcurrency
        rapid_lookup = $report.RapidLookup
        catalog_mutations = $report.CatalogMutations
        interactions = $report.Interactions
        audio_resource_observations = $audioResourceObservations
        themes = $report.Themes
        compatibility_query_count = @($report.CompatibilityQueries).Count
        real_dictionary_canaries = $canaryResults
        missing_canary_capabilities = $missingCanaryCapabilities
    }
    $summaryPath = Join-Path $resultRoot 'baseline-summary.json'
    $summary |
        ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Copy-Item -LiteralPath $rawReport -Destination (
        Join-Path $resultRoot 'baseline-raw.json')
    $screenshots = Join-Path (Split-Path $rawReport -Parent) 'screenshots'
    if (Test-Path -LiteralPath $screenshots -PathType Container) {
        Copy-Item -LiteralPath $screenshots -Destination $resultRoot -Recurse
    }
    Copy-Item -LiteralPath $logs -Destination (Join-Path $resultRoot 'logs') -Recurse

    $markdown = @"
# VDict remediation 0B baseline

- Run: ``$runId``
- Warm samples per performance scenario: ``$Samples``
- Outlier rule: none removed
- Percentile rule: nearest-rank
- WebView2: ``$($report.Environment.WebView2Runtime)``
- Fixture semantic SHA-256:
  ``0856edfd57fbc009b1c4ef9f66ee182d5ed0be2e83d13f40edb1f2b6b73407ca``
- Real dictionary canaries executed: ``$($canaryResults.Count)`` (requires at least two for final acceptance)

Full metrics and current Settings/Catalog/Render behavior are in
``baseline-summary.json`` and ``baseline-raw.json``.
"@
    Set-Content -LiteralPath (Join-Path $resultRoot 'README.md') `
        -Value $markdown `
        -Encoding UTF8

    $marker = Join-Path $workRoot '.vdict-remediation-work'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'remediation-0b') {
        throw "Refusing to clean unowned work directory: $workRoot"
    }
    Remove-Item -LiteralPath $workRoot -Recurse -Force
    Write-Host "Phase 0B baseline completed: $resultRoot"
}
catch {
    Write-Warning "Phase 0B failed; isolated work directory retained: $workRoot"
    throw
}
