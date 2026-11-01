@echo off
setlocal EnableExtensions
cd /d "%~dp0"
:menu
cls
echo ==============================================
echo   BF6 Universal Optimizer Package v1.4.0
echo ==============================================
echo.
echo  1 - Analyze (read only)
echo  2 - Apply Auto (no administrator required)
echo  3 - Status / Verify last successful Apply
echo  4 - Restore Last Successful Snapshot
echo  5 - Restore Original Baseline
echo  6 - Open Backups
echo  7 - Open Reports
echo  0 - Exit
echo.
set "choice="
set /p "choice=Select: "
if "%choice%"=="0" exit /b 0
if "%choice%"=="1" call "%~dp001_ANALYZE.cmd"
if "%choice%"=="2" call "%~dp002_APPLY_AUTO.cmd"
if "%choice%"=="3" call "%~dp003_STATUS_VERIFY.cmd"
if "%choice%"=="4" call "%~dp004_RESTORE_LAST_SNAPSHOT.cmd"
if "%choice%"=="5" call "%~dp005_RESTORE_ORIGINAL.cmd"
if "%choice%"=="6" call "%~dp006_OPEN_BACKUPS.cmd"
if "%choice%"=="7" call "%~dp007_OPEN_REPORTS.cmd"
goto menu
