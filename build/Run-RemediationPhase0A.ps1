[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\remediation-fixture'),

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [Parameter()]
    [switch] $SkipAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$generatorProject = Join-Path $repositoryRoot 'tools\VDict.RemediationFixture\VDict.RemediationFixture.csproj'
$testProject = Join-Path $repositoryRoot 'tests\VDict.Infrastructure.Tests\VDict.Infrastructure.Tests.csproj'

$generatorArguments = @(
    'run',
    '--project', $generatorProject,
    '--configuration', $Configuration
)
if ($SkipAudit) {
    $generatorArguments += '-p:NuGetAudit=false'
}
$generatorArguments += @('--', 'generate', '--output', $output)

& dotnet @generatorArguments
if ($LASTEXITCODE -ne 0) {
    throw "Fixture generation or validation failed with exit code $LASTEXITCODE."
}

$testArguments = @(
    'test',
    $testProject,
    '--configuration', $Configuration,
    '--filter', 'FullyQualifiedName~RemediationFixtureTests'
)
if ($SkipAudit) {
    $testArguments += '-p:NuGetAudit=false'
}

& dotnet @testArguments
if ($LASTEXITCODE -ne 0) {
    throw "Fixture regression tests failed with exit code $LASTEXITCODE."
}

Write-Host "Phase 0A fixture generated and validated: $output"
