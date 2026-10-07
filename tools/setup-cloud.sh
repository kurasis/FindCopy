#!/usr/bin/env bash
set -euo pipefail
sdk_root=/workspace/.dotnet
sdk_version=8.0.425
sdk_sha512=934b8060a7190e5909ad1fd0785db542f487b3bbf6cdd14826b02095fdd0d0394298b1634085eff302928fccc33f7c1a7253e9b87df555fc36fce819bcd2e798
export DOTNET_CLI_HOME=/tmp/findcopy-dotnet-cli
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES=/workspace/.nuget/packages
if [ ! -x "$sdk_root/dotnet" ] || [ "$("$sdk_root/dotnet" --version)" != "$sdk_version" ]; then
  sdk_archive=$(mktemp /tmp/findcopy-sdk.XXXXXX.tar.gz)
  trap 'rm -f "$sdk_archive"' EXIT
  curl --fail --silent --show-error --location "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdk_version/dotnet-sdk-$sdk_version-linux-x64.tar.gz" -o "$sdk_archive"
  printf '%s  %s\n' "$sdk_sha512" "$sdk_archive" | sha512sum --check -
  mkdir -p "$sdk_root"
  tar -xzf "$sdk_archive" -C "$sdk_root"
  rm -f "$sdk_archive"
  trap - EXIT
fi
export PATH="$sdk_root:$PATH"
cd /workspace/FindCopy
dotnet restore FindCopy.sln
dotnet build FindCopy.sln -c Release --no-restore -m:2
dotnet run -c Release --no-build --project tests/FindCopy.Tests
dotnet run -c Release --no-build --project tests/FindCopy.Audit
