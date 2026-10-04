@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Acceptance.ps1" %*
set "acceptance_exit=%ERRORLEVEL%"
echo.
echo Acceptance exit code: %acceptance_exit%
pause
exit /b %acceptance_exit%
