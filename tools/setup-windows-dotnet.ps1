[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$version = '8.0.425'
$hash = '2a75864daac54fe8360498d0a7260e3f8d851c3d811d864d7229f00ae51eac9e534998b7c3d221fc423319b9c8147dd43fa80690c969e43b2045cc2f1f93e537'
# SHA-512 from Microsoft's .NET 8 release metadata for this exact ARM64 SDK zip.
$root = Join-Path $env:RUNNER_TEMP 'FindCopy-dotnet-arm64'
$archive = Join-Path $env:RUNNER_TEMP 'FindCopy-dotnet-arm64.zip'
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    & curl.exe --fail --silent --show-error --location "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/dotnet-sdk-$version-win-arm64.zip" -o $archive
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the Windows ARM64 SDK.' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $hash) { throw 'SDK SHA-512 verification failed.' }
    & tar.exe -xf $archive -C $root
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified SDK.' }
    $sdk = Join-Path $root 'dotnet.exe'
    if ((& $sdk --version) -ne $version -or $LASTEXITCODE -ne 0) { throw 'Unexpected SDK version.' }
    & $sdk --info
    $root | Out-File -FilePath $env:GITHUB_PATH -Append -Encoding utf8
    "DOTNET_ROOT=$root" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
    "DOTNET_ROOT_ARM64=$root" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
finally { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
