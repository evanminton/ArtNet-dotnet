@echo off
rem Builds everything in Debug and Release, runs the tests and writes artifacts\build-log.txt.
rem Usage: build.cmd [Debug|Release]   (no argument = both). set ARTNET_ANDROID=1 to also build the Android app.
setlocal EnableDelayedExpansion
cd /d "%~dp0"
if not exist artifacts mkdir artifacts
set "LOG=%~dp0artifacts\build-log.txt"
set "SUMMARY="
set "FAILED=0"
set "CONFIGS=Debug Release"
if not "%~1"=="" set "CONFIGS=%~1"

echo ArtNet-dotnet build %DATE% %TIME% > "%LOG%"
dotnet --version >> "%LOG%" 2>&1

for %%C in (%CONFIGS%) do (
  call :step %%C "library" build src\ArtNet\ArtNet.csproj -c %%C
  call :step %%C "monitor" build tools\ArtNet.Monitor\ArtNet.Monitor.csproj -c %%C
  call :step %%C "tests" test tests\ArtNet.Tests\ArtNet.Tests.csproj -c %%C
  if exist samples\ArtNet.Maui\ArtNet.Maui.csproj (
    call :step %%C "maui-windows" build samples\ArtNet.Maui\ArtNet.Maui.csproj -c %%C -f net10.0-windows10.0.19041.0
    if "%ARTNET_ANDROID%"=="1" (
      rem MSBuild reads environment variables as properties; cmd would split "-p:Name=value" at the "=".
      set "IncludeAndroid=true"
      call :step %%C "maui-android" build samples\ArtNet.Maui\ArtNet.Maui.csproj -c %%C -f net10.0-android
      set "IncludeAndroid="
    ) else (
      set "SUMMARY=!SUMMARY! "skip %%C maui-android - set ARTNET_ANDROID=1 to build Android""
    )
  )
)

echo.>> "%LOG%"
echo ===== SUMMARY =====>> "%LOG%"
for %%S in (!SUMMARY!) do echo %%~S>> "%LOG%"
if "%FAILED%"=="0" (echo ALL OK>> "%LOG%") else (echo FAILURES: %FAILED%>> "%LOG%")
echo Done. See artifacts\build-log.txt
exit /b %FAILED%

:step
set "CFG=%~1"
set "NAME=%~2"
shift & shift
set "ARGS="
:collect
if "%~1"=="" goto run
set "ARGS=!ARGS! %1"
shift
goto collect
:run
echo.>> "%LOG%"
echo ===== !CFG! !NAME!: dotnet!ARGS! =====>> "%LOG%"
dotnet!ARGS! -nologo -v minimal -clp:NoSummary >> "%LOG%" 2>&1
if errorlevel 1 (
  set /a FAILED+=1
  set "SUMMARY=!SUMMARY! "FAIL !CFG! !NAME!""
) else (
  set "SUMMARY=!SUMMARY! "ok   !CFG! !NAME!""
)
exit /b 0
