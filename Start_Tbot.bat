@echo off
setlocal
cd /d "%~dp0"

echo Starting Tbot Ultra C# Desktop...
echo.

"C:\Program Files\dotnet\dotnet.exe" --list-sdks >nul 2>nul
if errorlevel 1 (
    echo .NET SDK is missing. Install .NET 8 SDK and try again.
    pause
    exit /b 1
)

"C:\Program Files\dotnet\dotnet.exe" run --project src\TbotUltra.Desktop\TbotUltra.Desktop.csproj -c Debug
if errorlevel 1 (
    echo.
    echo Failed to start the desktop app.
    pause
    exit /b 1
)

exit /b 0
