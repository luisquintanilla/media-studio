@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File "%~dp0Start-MediaStudio.ps1" %*
set "result=%errorlevel%"
if not "%result%"=="0" pause
exit /b %result%
