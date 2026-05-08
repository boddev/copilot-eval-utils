@echo off
setlocal

where pwsh >NUL 2>NUL
if %ERRORLEVEL% EQU 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-environment-eval.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-environment-eval.ps1" %*
)

exit /b %ERRORLEVEL%
