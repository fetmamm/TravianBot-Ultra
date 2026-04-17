@echo off
setlocal
cd /d "%~dp0"

echo Starting Tbot Ultra...
echo.

if not exist ".env" (
    echo No .env file found.
    echo Creating .env from .env.example.
    copy ".env.example" ".env" >nul
)

if not exist ".venv\Scripts\python.exe" (
    echo Creating local Python environment...
    py -m venv .venv
    if errorlevel 1 goto error
)

".venv\Scripts\python.exe" -m pip --version >nul 2>nul
if errorlevel 1 (
    echo Installing pip in local Python environment...
    ".venv\Scripts\python.exe" -m ensurepip --upgrade --default-pip
    if errorlevel 1 goto error
)

".venv\Scripts\python.exe" -c "import playwright" >nul 2>nul
if errorlevel 1 (
    echo Installing Python packages...
    ".venv\Scripts\python.exe" -m pip install -r requirements.txt
    if errorlevel 1 goto error
)

echo Checking Playwright browser...
".venv\Scripts\python.exe" -m playwright install chromium
if errorlevel 1 goto error

echo.
echo Opening Tbot Ultra UI...
".venv\Scripts\python.exe" run.py ui
echo.
pause
exit /b 0

:error
echo.
echo Something went wrong. Read the error above.
pause
exit /b 1
