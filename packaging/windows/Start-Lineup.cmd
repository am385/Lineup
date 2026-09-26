@echo off
setlocal

set "LINEUP_DATA_ROOT=%LOCALAPPDATA%\Lineup"
if not exist "%LINEUP_DATA_ROOT%\data" mkdir "%LINEUP_DATA_ROOT%\data"
if errorlevel 1 goto :error
if not exist "%LINEUP_DATA_ROOT%\xmltv" mkdir "%LINEUP_DATA_ROOT%\xmltv"
if errorlevel 1 goto :error
if not exist "%LINEUP_DATA_ROOT%\transient" mkdir "%LINEUP_DATA_ROOT%\transient"
if errorlevel 1 goto :error

set "ASPNETCORE_ENVIRONMENT=Production"
set "ASPNETCORE_HTTP_PORTS="
set "Lineup__AppDataPath=%LINEUP_DATA_ROOT%\data"
set "Lineup__XmltvPath=%LINEUP_DATA_ROOT%\xmltv"
set "Lineup__TransientPath=%LINEUP_DATA_ROOT%\transient"

if not exist "%~dp0app\Lineup.Web.exe" (
    echo Lineup.Web.exe is missing. Extract the complete ZIP before starting Lineup.
    goto :error
)

start "" /B powershell.exe -NoProfile -WindowStyle Hidden -Command "Start-Sleep -Seconds 3; Start-Process 'http://localhost:8080'"

pushd "%~dp0app"
"%~dp0app\Lineup.Web.exe"
set "LINEUP_EXIT_CODE=%ERRORLEVEL%"
popd

if not "%LINEUP_EXIT_CODE%"=="0" (
    echo Lineup exited with code %LINEUP_EXIT_CODE%.
    goto :error
)

exit /b %LINEUP_EXIT_CODE%

:error
echo.
echo Lineup could not be started. Review the message above, then press any key to close.
pause >nul
exit /b 1
