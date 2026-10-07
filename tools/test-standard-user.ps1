[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedExe,
    [Parameter(Mandatory)][string]$UiRunner,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishedExe).Path
$runner = (Resolve-Path -LiteralPath $UiRunner).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = 'FindCopyAcceptance'
if (Get-LocalUser -Name $name -ErrorAction SilentlyContinue) { throw 'Acceptance account already exists; refusing to modify it.' }
$secret = ConvertTo-SecureString (([Guid]::NewGuid().ToString('N')) + 'Aa1!') -AsPlainText -Force
$user = New-LocalUser -Name $name -Password $secret -AccountNeverExpires -PasswordNeverExpires
$process = $null
try {
    $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545'
    Add-LocalGroupMember -Group $usersGroup -Member $user
    $repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    & icacls $repository /grant "${name}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant test account read access.' }
    & icacls $output /grant "${name}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant test account artifact access.' }
    $credential = [PSCredential]::new("$env:COMPUTERNAME\$name", $secret)
    $arguments = '"{0}" --require-standard-user --published-exe "{1}"' -f $output, $published
    $process = Start-Process -FilePath $runner -ArgumentList $arguments -Credential $credential -LoadUserProfile `
        -WorkingDirectory (Split-Path $runner) -PassThru -RedirectStandardOutput (Join-Path $output 'stdout.txt') `
        -RedirectStandardError (Join-Path $output 'stderr.txt')
    if (-not $process.WaitForExit(240000)) { $process.Kill($true); throw 'Standard-user acceptance timed out.' }
    Get-Content (Join-Path $output 'stdout.txt')
    Get-Content (Join-Path $output 'stderr.txt')
    if ($process.ExitCode -ne 0) { throw "Standard-user acceptance failed: $($process.ExitCode)." }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill($true) }
    Remove-LocalUser -Name $name
}
