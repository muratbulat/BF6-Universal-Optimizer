@echo off
setlocal EnableExtensions
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BF6_Universal_AutoOptimizer_v1.4.0.ps1" -Mode RestoreOriginal
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" echo [FAIL] RestoreOriginal returned exit code %RC%. Open Reports for details.
if "%RC%"=="0" echo [ OK ] RestoreOriginal completed.
echo.
pause
exit /b %RC%
