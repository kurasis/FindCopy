[CmdletBinding()]
param([Parameter(Mandatory)][string]$TestsExe, [Parameter(Mandatory)][string]$BenchExe,
    [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$tests = (Resolve-Path -LiteralPath $TestsExe).Path
$bench = (Resolve-Path -LiteralPath $BenchExe).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = 'FindCopy-' + [Guid]::NewGuid().ToString('N')
$data = Join-Path $env:TEMP $name
New-Item -ItemType Directory -Path $data | Out-Null
$created = $false; $previousRoot = $env:FINDCOPY_SMB_ROOT
try {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    New-SmbShare -Name $name -Path $data -FullAccess $user | Format-List
    $created = $true; $env:FINDCOPY_SMB_ROOT = "\\localhost\$name"
    & $tests --network-only | Tee-Object -FilePath (Join-Path $output 'network-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw "SMB acceptance failed: $LASTEXITCODE." }
    1..4 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $data "fixture-$_.txt"), 'SMB harness fixture') }
    & $bench $output --scenario H --path $env:FINDCOPY_SMB_ROOT --cache `
        --label 'Windows 11 loopback SMB; functional acceptance, not physical network calibration' `
        --os-cache-state warm | Tee-Object -FilePath (Join-Path $output 'network-bench.txt')
    if ($LASTEXITCODE -ne 0) { throw "SMB H sweep failed: $LASTEXITCODE." }
}
finally {
    $env:FINDCOPY_SMB_ROOT = $previousRoot
    if ($created) { Remove-SmbShare -Name $name -Force }
    Remove-Item -LiteralPath $data -Recurse -Force
}
