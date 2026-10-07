[CmdletBinding()]
param([string]$OutputPath = 'windows-environment.json')
$ErrorActionPreference = 'Stop'
Get-ComputerInfo | Select-Object WindowsProductName, OsArchitecture | Format-List
$os = Get-CimInstance Win32_OperatingSystem
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$os | Select-Object Caption, Version, BuildNumber, ProductType, TotalVisibleMemorySize | Format-List
if ($os.ProductType -ne 1 -or [int]$os.BuildNumber -lt 22000 -or $os.Caption -notmatch 'Windows 11' -or $architecture -ne 'Arm64') {
    throw "Acceptance requires Windows 11 ARM64 client; observed $($os.Caption), build $($os.BuildNumber), $architecture."
}
$volumes = @(Get-Volume | Select-Object DriveLetter, FileSystem, SizeRemaining, Size)
$volumes | Format-Table
[ordered]@{
    utc = [DateTime]::UtcNow.ToString('O'); caption = $os.Caption; version = $os.Version
    build = $os.BuildNumber; productType = $os.ProductType; osArchitecture = $architecture
    shellArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    cpuCount = [Environment]::ProcessorCount; memoryKiB = $os.TotalVisibleMemorySize
    imageVersion = $env:ImageVersion; imageOS = $env:ImageOS; volumes = $volumes
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
