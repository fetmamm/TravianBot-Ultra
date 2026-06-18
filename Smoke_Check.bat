@echo off
setlocal
cd /d "%~dp0"
set "SMOKE_EXIT_CODE=0"

echo Running smoke check...
"C:\Program Files\dotnet\dotnet.exe" build TbotUltra.sln -c Debug
if errorlevel 1 (
  echo Build failed.
  set "SMOKE_EXIT_CODE=1"
  goto done
)

"C:\Program Files\dotnet\dotnet.exe" test src\TbotUltra.Worker.Tests\TbotUltra.Worker.Tests.csproj -c Debug --no-build
if errorlevel 1 (
  echo Tests failed.
  set "SMOKE_EXIT_CODE=1"
  goto done
)

echo Smoke check OK.

:done
echo.
echo Smoke check finished. Press any key to close this window.
pause >nul
exit /b %SMOKE_EXIT_CODE%
