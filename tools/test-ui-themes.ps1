[CmdletBinding()]
param([Parameter(Mandatory)][string]$UiRunner, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$runner = (Resolve-Path -LiteralPath $UiRunner).Path
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
$old = (Get-ItemProperty -Path $key -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
try {
    foreach ($theme in @('light', 'dark')) {
        $value = if ($theme -eq 'light') { 1 } else { 0 }
        Set-ItemProperty -Path $key -Name AppsUseLightTheme -Type DWord -Value $value
        $output = Join-Path $OutputDirectory $theme
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Write-Host "WINDOWS_APP_THEME: $theme; AppsUseLightTheme=$value"
        & $runner $output | Tee-Object -FilePath (Join-Path $output 'stdout.txt')
        if ($LASTEXITCODE -ne 0) { throw "WPF acceptance failed in $theme mode: $LASTEXITCODE." }
    }
}
finally {
    if ($null -eq $old) { Remove-ItemProperty -Path $key -Name AppsUseLightTheme }
    else { Set-ItemProperty -Path $key -Name AppsUseLightTheme -Type DWord -Value $old }
}
