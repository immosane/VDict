[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $BaselineSummary,

    [Parameter()]
    [ValidateSet('fixture', 'real-representative')]
    [string] $WorkloadKind = 'fixture',

    [Parameter()]
    [string] $ScenarioName = 'audio-playback',

    [Parameter()]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\remediation-phase5b')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$largeResourceBytes = 8MB
$maximumRequestedFraction = 0.25
$minimumActualWorkFraction = 0.90
$maximumColdP95Milliseconds = 300.0
$minimumMemoryImpactBytes = 16MB

function Get-Number {
    param(
        [Parameter()][object] $Object,
        [Parameter(Mandatory)][string] $Name,
        [Parameter()][double] $Default = 0
    )

    if ($null -eq $Object) {
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return $Default
    }
    $value = 0.0
    if ([double]::TryParse(
            [string]$property.Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$value)) {
        return $value
    }
    return $Default
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)][double[]] $Values,
        [Parameter(Mandatory)][ValidateRange(0, 1)][double] $Percentile
    )

    if ($Values.Count -eq 0) {
        return $null
    }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling($Percentile * $sorted.Count) - 1)
    return [double]$sorted[$index]
}

function Get-RequestedBytes {
    param([Parameter(Mandatory)][object] $Observation)

    $contentRange = [string]$Observation.ContentRange
    if ($contentRange -match '^bytes (?<start>[0-9]+)-(?<end>[0-9]+)/[0-9]+$') {
        return [long]$Matches['end'] - [long]$Matches['start'] + 1
    }
    return [long](Get-Number -Object $Observation -Name 'ReturnedBytes')
}

function Test-RangeStreamingThreshold {
    param(
        [Parameter(Mandatory)][bool] $IsRealRepresentative,
        [Parameter(Mandatory)][long] $FullLength,
        [Parameter(Mandatory)][long] $RequestedBytes,
        [Parameter(Mandatory)][long] $ActualWorkBytes,
        [Parameter(Mandatory)][double] $ColdP95Milliseconds,
        [Parameter(Mandatory)][long] $ManagedHeapPeakDelta,
        [Parameter(Mandatory)][long] $WorkingSetPeakDelta
    )

    $requestedFraction = if ($FullLength -gt 0) {
        $RequestedBytes / [double]$FullLength
    } else {
        1.0
    }
    $actualWorkFraction = if ($FullLength -gt 0) {
        $ActualWorkBytes / [double]$FullLength
    } else {
        0.0
    }
    $performanceImpact =
        $ColdP95Milliseconds -gt $maximumColdP95Milliseconds -or
        $ManagedHeapPeakDelta -ge $minimumMemoryImpactBytes -or
        $WorkingSetPeakDelta -ge $minimumMemoryImpactBytes
    return $IsRealRepresentative -and
        $FullLength -ge $largeResourceBytes -and
        $requestedFraction -le $maximumRequestedFraction -and
        $actualWorkFraction -ge $minimumActualWorkFraction -and
        $performanceImpact
}

# Guard the most important policy invariant every time the analyzer runs:
# synthetic pressure data can validate the path but can never authorize production work.
$syntheticPolicyArguments = @{
    FullLength = 8MB
    RequestedBytes = 1MB
    ActualWorkBytes = 8MB
    ColdP95Milliseconds = 301
    ManagedHeapPeakDelta = 16MB
    WorkingSetPeakDelta = 0
}
if (Test-RangeStreamingThreshold -IsRealRepresentative $false @syntheticPolicyArguments) {
    throw 'Phase 5B policy self-test failed: fixture data triggered production streaming.'
}
if (-not (Test-RangeStreamingThreshold -IsRealRepresentative $true @syntheticPolicyArguments)) {
    throw 'Phase 5B policy self-test failed: qualifying representative data was rejected.'
}

$resolvedSummaries = @(
    $BaselineSummary | ForEach-Object {
        (Resolve-Path -LiteralPath $_).Path
    }
)
$coldPlayback = [System.Collections.Generic.List[double]]::new()
$warmPlayback = [System.Collections.Generic.List[double]]::new()
$repeatPlayback = [System.Collections.Generic.List[double]]::new()
$observations = [System.Collections.Generic.List[object]]::new()

foreach ($summaryPath in $resolvedSummaries) {
    $summary = Get-Content -LiteralPath $summaryPath -Encoding UTF8 -Raw |
        ConvertFrom-Json
    if ($null -ne $summary.Interactions -and
        -not [string]::IsNullOrWhiteSpace([string]$summary.Interactions.Audio)) {
        $audio = [string]$summary.Interactions.Audio | ConvertFrom-Json
        $recordsProperty = $audio.PSObject.Properties['records']
        if ($null -eq $recordsProperty) {
            continue
        }
        foreach ($record in @($recordsProperty.Value)) {
            $milliseconds = Get-Number -Object $record -Name 'clickToPlayingMs' -Default -1
            if ($milliseconds -lt 0) {
                continue
            }
            $sequence = [int](Get-Number -Object $record -Name 'sequence')
            if ($sequence -eq 1) {
                $coldPlayback.Add($milliseconds)
            } elseif ($sequence -eq 2) {
                $warmPlayback.Add($milliseconds)
            } else {
                $repeatPlayback.Add($milliseconds)
            }
        }
    }

    foreach ($observation in @($summary.audio_resource_observations)) {
        $observations.Add($observation)
    }
}

$coldP95 = Get-Percentile -Values $coldPlayback.ToArray() -Percentile 0.95
$coldP95ForDecision = if ($null -eq $coldP95) { 0.0 } else { [double]$coldP95 }
$isRealRepresentative = $WorkloadKind -eq 'real-representative'
$resourceResults = @()
$thresholdMatched = $false
foreach ($observation in $observations) {
    $fullLength = [long](Get-Number -Object $observation -Name 'FullLength')
    $requestedBytes = Get-RequestedBytes -Observation $observation
    $sourceBytes = [long](Get-Number -Object $observation -Name 'SourceBytesRead')
    $decodedBytes = [long](Get-Number -Object $observation -Name 'RecordBlockBytesDecoded')
    $materializedBytes = [long](Get-Number -Object $observation -Name 'ResourceBytesMaterialized')
    $returnedBytes = [long](Get-Number -Object $observation -Name 'ReturnedBytes')
    $responseCopiedBytes = [long](Get-Number `
        -Object $observation `
        -Name 'ResponseStreamCopiedBytes' `
        -Default ($returnedBytes * 2))
    $managedHeapPeakDelta =
        [long](Get-Number -Object $observation -Name 'ManagedHeapPeakDelta')
    $workingSetPeakDelta =
        [long](Get-Number -Object $observation -Name 'WorkingSetPeakDelta')
    $actualWorkBytes = [long](
        @($sourceBytes, $decodedBytes, $materializedBytes, $responseCopiedBytes) |
            Measure-Object -Maximum
    ).Maximum
    $matched = Test-RangeStreamingThreshold `
        -IsRealRepresentative $isRealRepresentative `
        -FullLength $fullLength `
        -RequestedBytes $requestedBytes `
        -ActualWorkBytes $actualWorkBytes `
        -ColdP95Milliseconds $coldP95ForDecision `
        -ManagedHeapPeakDelta $managedHeapPeakDelta `
        -WorkingSetPeakDelta $workingSetPeakDelta
    $thresholdMatched = $thresholdMatched -or $matched
    $resourceResults += [ordered]@{
        method = [string]$observation.Method
        path = [string]$observation.Path
        source = [string]$observation.Source
        range = [string]$observation.Range
        full_length = $fullLength
        requested_bytes = $requestedBytes
        requested_fraction = if ($fullLength -gt 0) {
            [Math]::Round($requestedBytes / [double]$fullLength, 4)
        } else { 0 }
        source_bytes_read = $sourceBytes
        record_block_bytes_decoded = $decodedBytes
        resource_bytes_materialized = $materializedBytes
        returned_bytes = $returnedBytes
        response_stream_copied_bytes = $responseCopiedBytes
        known_array_and_stream_copies =
            [int](Get-Number -Object $observation -Name 'ResourceArrayCopies') +
            [int](Get-Number `
                -Object $observation `
                -Name 'ResponseStreamCopyCount' `
                -Default $(if ($returnedBytes -gt 0) { 2 } else { 0 }))
        actual_work_fraction = if ($fullLength -gt 0) {
            [Math]::Round(
                [Math]::Min(1.0, $actualWorkBytes / [double]$fullLength),
                4)
        } else { 0 }
        managed_heap_peak_delta = $managedHeapPeakDelta
        working_set_peak_delta = $workingSetPeakDelta
        resolve_ms = Get-Number -Object $observation -Name 'ResolveElapsedMs'
        response_ms = Get-Number -Object $observation -Name 'ElapsedMs'
        threshold_matched = $matched
    }
}

function Get-PlaybackSummary {
    param([Parameter(Mandatory)][double[]] $Values)

    return [ordered]@{
        n = $Values.Count
        p50_ms = if ($Values.Count -eq 0) {
            $null
        } else {
            [Math]::Round((Get-Percentile -Values $Values -Percentile 0.50), 3)
        }
        p95_ms = if ($Values.Count -eq 0) {
            $null
        } else {
            [Math]::Round((Get-Percentile -Values $Values -Percentile 0.95), 3)
        }
        max_ms = if ($Values.Count -eq 0) {
            $null
        } else {
            [Math]::Round((($Values | Measure-Object -Maximum).Maximum), 3)
        }
    }
}

$decision = if ($thresholdMatched) {
    'implement-range-streaming'
} elseif ($isRealRepresentative) {
    'retain-existing-path'
} else {
    'fixture-only-no-production-trigger'
}
$result = [ordered]@{
    schema = 'phase5b-audio-measurement-v1'
    generated_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    scenario = $ScenarioName
    workload_kind = $WorkloadKind
    source_summaries = $resolvedSummaries
    thresholds = [ordered]@{
        minimum_real_resource_bytes = $largeResourceBytes
        maximum_requested_fraction = $maximumRequestedFraction
        minimum_actual_work_fraction = $minimumActualWorkFraction
        maximum_cold_p95_ms = $maximumColdP95Milliseconds
        minimum_memory_impact_bytes = $minimumMemoryImpactBytes
    }
    playback = [ordered]@{
        cold = Get-PlaybackSummary -Values $coldPlayback.ToArray()
        warm = Get-PlaybackSummary -Values $warmPlayback.ToArray()
        repeat = Get-PlaybackSummary -Values $repeatPlayback.ToArray()
    }
    resources = $resourceResults
    decision = $decision
    production_range_streaming_authorized = $thresholdMatched
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$jsonPath = Join-Path $outputRoot 'phase5b-summary.json'
$markdownPath = Join-Path $outputRoot 'phase5b-summary.md'
$result | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $jsonPath -Encoding UTF8

$markdown = @"
# VDict Phase 5B audio measurement

- Scenario: ``$ScenarioName``
- Workload: ``$WorkloadKind``
- Source runs: $($resolvedSummaries.Count)
- Audio resource observations: $($resourceResults.Count)
- Cold click-to-playing samples: $($coldPlayback.Count)
- Warm click-to-playing samples: $($warmPlayback.Count)
- Repeat click-to-playing samples: $($repeatPlayback.Count)
- Decision: ``$decision``
- Production Range/streaming authorized: ``$thresholdMatched``

The analyzer requires all Phase 5B conditions in the same scenario. Fixture data can
validate measurement and pressure behavior, but cannot authorize a production
Range/streaming implementation.
"@
$markdown | Set-Content -LiteralPath $markdownPath -Encoding UTF8

Write-Host "Phase 5B summary: $jsonPath"
Write-Host "Decision: $decision"
