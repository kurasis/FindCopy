[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$script = (Resolve-Path (Join-Path $PSScriptRoot '../build.bat')).Path
$pin = Get-Content (Join-Path $PSScriptRoot '../global.json') -Raw
$root = Join-Path ([IO.Path]::GetTempPath()) ('FindCopy-build-smoke-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($scenario in @('missing-sdk', 'publish-failure')) {
        $fixture = Join-Path $root $scenario
        New-Item -ItemType Directory -Path $fixture -Force | Out-Null
        Copy-Item -LiteralPath $script -Destination (Join-Path $fixture 'build.bat')
        $config = $pin | ConvertFrom-Json
        if ($scenario -eq 'missing-sdk') { $config.sdk.version = '9.9.999' }
        $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'global.json') -Encoding utf8
        # The second fixture deliberately lacks src/FindCopy.App, making publish fail without a restore.
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $env:ComSpec
        $start.Arguments = '/d /c build.bat build nopause'
        $start.WorkingDirectory = $fixture
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $start.RedirectStandardOutput = $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(15000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "build.bat did not exit in $scenario mode."
            }
            $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            Write-Host $text
            if ($process.ExitCode -ne 1 -or $text -match 'Press any key') {
                throw "build.bat did not fail non-interactively in $scenario mode."
            }
            if ($scenario -eq 'missing-sdk') {
                if ($text -notmatch 'SDK required by global.json' -or $text -match '=== Publish') {
                    throw 'SDK mismatch was not detected before publishing.'
                }
            }
            elseif ($text -notmatch '=== Publish' -or $text -notmatch 'Build failed') {
                throw 'Publish failure did not reach the error branch.'
            }
            Write-Host "BUILD_SCRIPT: $scenario passed"
        }
        finally { $process.Dispose() }
    }
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
