Tbot Ultra release package

1. Extract this ZIP to a normal folder.
2. Copy .env.example to .env.
3. Edit .env and config\bot.json with your real account and server settings.
4. Start TbotUltra.Desktop.exe.

Notes:
- The app expects config files next to the exe in this extracted folder.
- On first browser-based run the app can install Chromium automatically by using playwright.ps1.
- Captcha auto-solve is disabled in the release template. Enable it only after you have set up the local Python solver runtime.
