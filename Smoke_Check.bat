@echo off
setlocal
cd /d "%~dp0"
set "SMOKE_EXIT_CODE=0"

echo Running smoke check...
powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\Build-Check.ps1"
if errorlevel 1 (
  echo Build failed.
  set "SMOKE_EXIT_CODE=1"
  goto done
)

powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\Run-Tests.ps1" -Project "src\TbotUltra.Worker.Tests\TbotUltra.Worker.Tests.csproj"
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
