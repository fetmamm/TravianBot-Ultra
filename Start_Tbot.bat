@echo off
setlocal
cd /d "%~dp0"

echo Starting Tbot Ultra C# Desktop...
echo.

set "PROJECT=src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"
set "DEV_OUT=temp_build_out\dev-app"
set "EXE=%DEV_OUT%\TbotUltra.Desktop.exe"

"C:\Program Files\dotnet\dotnet.exe" --list-sdks >nul 2>nul
if errorlevel 1 (
    echo .NET SDK is missing. Install .NET 8 SDK and try again.
    pause
    exit /b 1
)

tasklist /FI "IMAGENAME eq TbotUltra.Desktop.exe" | find /I "TbotUltra.Desktop.exe" >nul 2>nul
if not errorlevel 1 (
    echo Tbot Ultra is already running. Leaving it open and activating the existing window.
    powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $null = $ws.AppActivate('Tbot Ultra')" >nul 2>nul
    exit /b 0
)

"C:\Program Files\dotnet\dotnet.exe" build "%PROJECT%" -c Debug -nologo -m:1 -o "%DEV_OUT%" --disable-build-servers -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildInParallel=false
if errorlevel 1 (
    echo.
    echo Build failed for desktop app.
    pause
    exit /b 1
)

"%EXE%"
if errorlevel 1 (
    echo.
    echo Failed to start built desktop exe.
    pause
    exit /b 1
)

exit /b 0
