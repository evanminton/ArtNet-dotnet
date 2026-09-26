@echo off
rem Same as build.cmd, including the Android app (needs: dotnet workload install maui-android).
setlocal
set "ARTNET_ANDROID=1"
call "%~dp0build.cmd" %*
