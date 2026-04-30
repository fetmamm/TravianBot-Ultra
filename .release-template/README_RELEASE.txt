Tbot Ultra installer package

1. Run the setup file.
2. Open the installed folder.
3. Edit .env and config\bot.json with your real account and server settings.
4. Start Tbot Ultra from the installed shortcut or TbotUltra.Desktop.exe.

Notes:
- The installer places config files next to the exe in the installed app folder.
- The default install folder is under your local user profile so the app can write logs, queue data and config updates.
- If .env or config files already exist from an older install, setup keeps them.
- On first browser-based run the app can install Chromium automatically by using playwright.ps1.
- Captcha auto-solve is disabled in the release template. Enable it only after you have set up the local Python solver runtime.
