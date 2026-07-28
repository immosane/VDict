$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$englishPath = Join-Path $repositoryRoot 'src\VDict.WinUI\Strings\en-US\Resources.resw'
$chinesePath = Join-Path $repositoryRoot 'src\VDict.WinUI\Strings\zh-CN\Resources.resw'

function Read-ResourceMap {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    [xml]$document = Get-Content -Raw -Encoding UTF8 -LiteralPath $Path
    $nodes = @($document.root.data)
    $duplicates = @(
        $nodes |
            Group-Object -Property name |
            Where-Object Count -gt 1 |
            ForEach-Object Name
    )
    if ($duplicates.Count -gt 0) {
        throw "Duplicate resource keys in $Path`: $($duplicates -join ', ')"
    }

    $resources = @{}
    foreach ($node in $nodes) {
        $resources[[string]$node.name] = [string]$node.value
    }

    return $resources
}

function Get-PlaceholderSet {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    return @(
        [regex]::Matches($Value, '\{(\d+)(?:[^}]*)\}') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique
    )
}

$english = Read-ResourceMap -Path $englishPath
$chinese = Read-ResourceMap -Path $chinesePath
$keyDifference = @(
    Compare-Object @($english.Keys | Sort-Object) @($chinese.Keys | Sort-Object)
)
if ($keyDifference.Count -gt 0) {
    throw "English and Chinese resource keys differ:`n$($keyDifference | Out-String)"
}

foreach ($key in $english.Keys) {
    $englishPlaceholders = @(Get-PlaceholderSet -Value $english[$key])
    $chinesePlaceholders = @(Get-PlaceholderSet -Value $chinese[$key])
    $placeholderDifference = @(
        Compare-Object $englishPlaceholders $chinesePlaceholders
    )
    if ($placeholderDifference.Count -gt 0) {
        throw "Placeholder mismatch for resource '$key'."
    }
}

$resourceReferencePattern =
    '(?i)\b[_A-Za-z]\w*localizer\.(?:Get|Format)\(\s*"([^"]+)"'
$xamlUidPattern = 'x:Uid="([^"]+)"'
$missing = [System.Collections.Generic.SortedSet[string]]::new(
    [System.StringComparer]::Ordinal)
$sourceRoot = Join-Path $repositoryRoot 'src'
$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' -and
            $_.Extension -in '.cs', '.xaml'
        }
)

foreach ($file in $sourceFiles) {
    $content = Get-Content -Raw -Encoding UTF8 -LiteralPath $file.FullName
    if ($file.Extension -eq '.cs') {
        foreach ($match in [regex]::Matches($content, $resourceReferencePattern)) {
            $key = $match.Groups[1].Value
            if (-not $english.ContainsKey($key)) {
                $null = $missing.Add("$key ($($file.FullName))")
            }
        }
    }
    else {
        foreach ($match in [regex]::Matches($content, $xamlUidPattern)) {
            $prefix = "$($match.Groups[1].Value)."
            $found = $false
            foreach ($key in $english.Keys) {
                if ($key.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                    $found = $true
                    break
                }
            }

            if (-not $found) {
                $null = $missing.Add("$prefix* ($($file.FullName))")
            }
        }
    }
}

if ($missing.Count -gt 0) {
    throw "Missing localization resources:`n$($missing -join [Environment]::NewLine)"
}

Write-Host (
    "Localization resources verified: {0} matching keys; placeholders and direct references are valid." `
        -f $english.Count)
