@echo off
rem Publishes the standalone Windows app (unpackaged, self-contained) to artifacts\desktop\<rid> and zips it.
rem Usage: publish-desktop.cmd [win-x64|win-arm64|win-x86]   (default win-x64). Log: artifacts\desktop-log.txt
setlocal
cd /d "%~dp0"
set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"
if not exist artifacts mkdir artifacts
set "LOG=%~dp0artifacts\desktop-log.txt"
set "OUT=%~dp0artifacts\desktop\%RID%"
set "ZIP=%~dp0artifacts\ArtNetDesktop-%RID%.zip"

echo Art-Net Desktop publish %RID% %DATE% %TIME% > "%LOG%"
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish apps\ArtNet.Desktop\ArtNet.Desktop.csproj -c Release -r %RID% -o "%OUT%" -nologo -v minimal >> "%LOG%" 2>&1
if errorlevel 1 (
  echo PUBLISH FAILED>> "%LOG%"
  echo Publish failed. See artifacts\desktop-log.txt
  exit /b 1
)
if exist "%ZIP%" del /q "%ZIP%"
rem Paths go through the environment so quotes in the repo path cannot break the PowerShell command.
powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; Compress-Archive -Path (Join-Path $env:OUT '*') -DestinationPath $env:ZIP" >> "%LOG%" 2>&1
if errorlevel 1 (
  echo ZIP FAILED>> "%LOG%"
  echo Zipping failed. See artifacts\desktop-log.txt
  exit /b 1
)
echo.>> "%LOG%"
echo OK: %OUT%\ArtNetDesktop.exe>> "%LOG%"
echo OK: %ZIP%>> "%LOG%"
echo Done: %OUT%\ArtNetDesktop.exe  (zip: artifacts\ArtNetDesktop-%RID%.zip)
exit /b 0
