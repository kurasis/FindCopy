[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('E', 'F', 'G', 'H')][string]$Scenario,
    [Parameter(Mandatory)][string]$Dataset,
    [Parameter(Mandatory)][string]$Work,
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$OsCacheState,
    [switch]$Cache,
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$datasetPath = (Resolve-Path -LiteralPath $Dataset).Path
if (-not (Test-Path -LiteralPath $datasetPath -PathType Container)) {
    throw 'Dataset must be an existing directory.'
}
$projectPath = Join-Path $PSScriptRoot 'FindCopy.Bench/FindCopy.Bench.csproj'
$benchArgs = @('run', '-c', 'Release', '--project', $projectPath)
if ($RuntimeIdentifier) { $benchArgs += @("-p:RuntimeIdentifier=$RuntimeIdentifier", '-p:SelfContained=true') }
$benchArgs += @('--',
    $Work, '--scenario', $Scenario, '--path', $datasetPath,
    '--label', $Label, '--os-cache-state', $OsCacheState)
if ($Cache) { $benchArgs += '--cache' }
& dotnet @benchArgs
if ($LASTEXITCODE -ne 0) { throw "Benchmark failed with exit code $LASTEXITCODE." }
