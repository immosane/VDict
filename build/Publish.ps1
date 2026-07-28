[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [Parameter()]
    [switch] $SkipAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'VDict.sln'
$artifacts = Join-Path $root 'artifacts'
$legacyMsixRoot = Join-Path $artifacts 'msix'
$portableName = "VDict-$Version-win-x64-portable"
$portableRoot = Join-Path (Join-Path $artifacts 'portable') $portableName
$zipPath = Join-Path $artifacts "$portableName.zip"
$checksumPath = Join-Path $artifacts 'SHA256SUMS.txt'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Remove-Artifact {
    param([Parameter(Mandatory)][string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $artifactPrefix = [System.IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith(
            $artifactPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
# Remove stale outputs from the retired MSIX release path.
Get-ChildItem -LiteralPath $artifacts -Filter '*.msix' -File -Recurse |
    ForEach-Object { Remove-Artifact -Path $_.FullName }
Remove-Artifact -Path $legacyMsixRoot
Remove-Artifact -Path $portableRoot
Remove-Artifact -Path $zipPath
Remove-Artifact -Path $checksumPath

Push-Location $root
try {
    $restoreArguments = @('restore', $solution)
    if ($SkipAudit) {
        $restoreArguments += '-p:NuGetAudit=false'
    }
    Invoke-DotNet -Arguments $restoreArguments
    Invoke-DotNet -Arguments @(
        'test', $solution,
        '--configuration', 'Release',
        '--no-restore'
    )
    Invoke-DotNet -Arguments @(
        'publish', 'src\VDict.WinUI\VDict.WinUI.csproj',
        '--configuration', 'Portable',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $portableRoot
    )

    Copy-Item -LiteralPath (Join-Path $root 'docs\PORTABLE.md') `
        -Destination (Join-Path $portableRoot 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') `
        -Destination (Join-Path $portableRoot 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') `
        -Destination (Join-Path $portableRoot 'THIRD-PARTY-NOTICES.md')
    Compress-Archive -LiteralPath $portableRoot -DestinationPath $zipPath `
        -CompressionLevel Optimal
}
finally {
    Pop-Location
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$zipHash  $([System.IO.Path]::GetFileName($zipPath))"
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$checksumLine$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Portable ZIP: $zipPath"
Write-Host "SHA-256: $checksumPath"
