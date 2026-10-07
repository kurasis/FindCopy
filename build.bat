@echo off
rem ============================================================
rem  FindCopy - build script
rem    build.bat          build FindCopy.exe into .\publish
rem    build.bat test     run the correctness tests, then build
rem    build.bat bench    also build the benchmark tool
rem  Needs .NET SDK 8 or newer: https://dotnet.microsoft.com/download/dotnet/8.0
rem ============================================================
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk
dotnet --list-sdks 2>nul | findstr /r /c:"^[89]\." /c:"^[1-9][0-9]\." >nul
if errorlevel 1 goto :nosdk

echo.
echo === FindCopy: build ===
for /f "delims=" %%v in ('dotnet --version') do echo .NET SDK %%v

if /i "%~1"=="test" (
  echo.
  echo === Correctness tests ===
  dotnet run -c Release --project tests\FindCopy.Tests\FindCopy.Tests.csproj
  if errorlevel 1 (
    echo.
    echo TESTS FAILED. Build stopped.
    goto :fail
  )
  dotnet run -c Release --project tests\FindCopy.Audit\FindCopy.Audit.csproj
  if errorlevel 1 (
    echo.
    echo ACCEPTANCE CHECKS FAILED. Build stopped.
    goto :fail
  )
)

echo.
echo === Publish FindCopy.exe ===
dotnet publish src\FindCopy.App\FindCopy.App.csproj -c Release -o publish
if errorlevel 1 goto :fail

if /i "%~1"=="bench" (
  echo.
  echo === Benchmark ===
  dotnet publish tools\FindCopy.Bench\FindCopy.Bench.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\bench
  if errorlevel 1 goto :fail
)

echo.
echo Done: "%CD%\publish\FindCopy.exe"
if /i not "%~2"=="nopause" pause
exit /b 0

:nosdk
echo.
echo .NET SDK 8 or newer was not found.
echo Download and install ".NET 8.0 SDK" (x64):
echo   https://dotnet.microsoft.com/download/dotnet/8.0
echo After installation, open a new terminal and run build.bat again.
pause
exit /b 1

:fail
echo.
echo Build failed.
pause
exit /b 1
