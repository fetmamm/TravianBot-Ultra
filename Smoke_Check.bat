@echo off
setlocal
cd /d "%~dp0"

echo Running smoke check...
"C:\Program Files\dotnet\dotnet.exe" build TbotUltra.sln -c Debug
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

"C:\Program Files\dotnet\dotnet.exe" test src\TbotUltra.Worker.Tests\TbotUltra.Worker.Tests.csproj -c Debug --no-build
if errorlevel 1 (
  echo Tests failed.
  exit /b 1
)

echo Smoke check OK.
exit /b 0
