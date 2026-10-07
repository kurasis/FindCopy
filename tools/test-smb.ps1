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
    $block = New-Object byte[] 1048576
    ([Random]::new(71)).NextBytes($block)
    foreach ($index in 1..4) {
        $stream = [IO.File]::Open((Join-Path $data "fixture-$index.bin"), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try { foreach ($chunk in 1..8) { $stream.Write($block, 0, $block.Length) }; $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    $csv = Join-Path $output 'bench-results-v3.csv'
    $previousRows = if (Test-Path -LiteralPath $csv) { @(Import-Csv -LiteralPath $csv -Delimiter ';').Count } else { 0 }
    & $bench $output --scenario H --path $env:FINDCOPY_SMB_ROOT --cache `
        --label 'Windows 11 loopback SMB; functional acceptance, not physical network calibration' `
        --os-cache-state warm | Tee-Object -FilePath (Join-Path $output 'network-bench.txt')
    if ($LASTEXITCODE -ne 0) { throw "SMB H sweep failed: $LASTEXITCODE." }
    $rows = @(Import-Csv -LiteralPath $csv -Delimiter ';' | Select-Object -Skip $previousRows)
    if ($rows.Count -ne 54) { throw "Expected 54 current SMB sweep rows, got $($rows.Count)." }
    $variants = @($rows | Group-Object -Property reader_limit,threshold_kib,buffer_kib)
    if ($variants.Count -ne 18) { throw 'SMB parameter sweep did not execute all 18 configurations.' }
    foreach ($variant in $variants) {
        $modes = @($variant.Group.run | Sort-Object)
        if (($modes -join ',') -ne 'cache-fill,cache-warm,nocache') { throw 'SMB cache modes are incomplete.' }
    }
    foreach ($row in $rows) {
        if ($row.scenario -ne 'H' -or $row.files -ne '4' -or $row.logical_bytes -ne '33554432' -or
            $row.groups -ne '1' -or $row.complete -ne 'True' -or $row.errors -ne '0' -or
            $row.changed -ne '0' -or $row.skipped -ne '0' -or -not $row.storage.StartsWith('Network:net:')) {
            throw 'SMB sweep lost candidates, physical groups, network classification, or completeness.'
        }
        $expectedContent = if ($row.run -eq 'cache-warm') { '0' } else { '34078720' }
        if ($row.content_bytes -ne $expectedContent) { throw 'SMB sweep did not perform full reads or reuse warm fingerprints.' }
        if ($row.run -eq 'cache-warm' -and $row.cache_hit_percent -ne '100') { throw 'SMB warm fingerprints were not fully reused.' }
    }
    Write-Host 'PASS SMB sweep: 18 configurations, 54 complete rows, one group, full reads and zero warm content I/O.'
}
finally {
    $env:FINDCOPY_SMB_ROOT = $previousRoot
    if ($created) { Remove-SmbShare -Name $name -Force }
    Remove-Item -LiteralPath $data -Recurse -Force
}
