@echo off
setlocal
cd /d "%~dp0"

echo Starting Tbot Ultra C# Desktop...
echo.

set "PROJECT=src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"
set "EXE=src\TbotUltra.Desktop\bin\Debug\net8.0-windows\TbotUltra.Desktop.exe"

"C:\Program Files\dotnet\dotnet.exe" --list-sdks >nul 2>nul
if errorlevel 1 (
    echo .NET SDK is missing. Install .NET 8 SDK and try again.
    pause
    exit /b 1
)

taskkill /IM TbotUltra.Desktop.exe /F >nul 2>nul
"C:\Program Files\dotnet\dotnet.exe" build "%PROJECT%" -c Debug -nologo -m:1 -p:NuGetAudit=false
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
