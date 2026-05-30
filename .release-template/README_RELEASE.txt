Tbot Ultra

There are two ways to get Tbot Ultra. Pick one.

== Option A: Installer (TbotUltra-Setup-...exe) ==
1. Run the setup file.
2. Open the installed folder.
3. Fill in the generated .env and config\bot.json with your real account and server settings.
4. Start Tbot Ultra from the installed shortcut or TbotUltra.Desktop.exe.

== Option B: Portable (tbot-ultra-win-x64-...-portable.zip) ==
No installation needed.
1. Extract the zip to a folder where you have write access (e.g. your Desktop, NOT Program Files).
2. Open the extracted folder.
3. Fill in .env and config\bot.json with your real account and server settings.
4. Run TbotUltra.Desktop.exe.

Running multiple accounts at the same time (portable):
- Extract a SEPARATE copy of the portable folder for each account
  (e.g. Tbot-account1\, Tbot-account2\).
- Each folder has its own config, account, browser session and logs, so they
  run fully isolated and can run simultaneously.
- Do NOT run two instances from the SAME folder - they would share config,
  logs and the browser session and conflict with each other.

Notes:
- Config files (.env, config\) live next to the exe in the app folder.
- Use a folder the app can write to (logs, queue data, config updates). The
  installer's default location under your user profile already allows this.
- If .env or config files already exist from an older copy, they are kept.
- On first browser-based run the app can install Chromium automatically via playwright.ps1.
- The captcha solver runtime is included in both downloads.
- Captcha auto-solve is disabled in the release template until you enable it in config.
