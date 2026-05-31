Tbot Ultra

There are two ways to get Tbot Ultra. Pick one.

== Option A: Installer (TbotUltra-Setup-...exe) ==
1. Run the setup file.
2. Start Tbot Ultra from the installed shortcut or Tbot Ultra.exe.
3. Add your account and server inside the app (Account -> Manage), then log in.

== Option B: Portable (tbot-ultra-win-x64-...-portable.zip) ==
No installation needed.
1. Extract the zip to a folder where you have write access (e.g. your Desktop, NOT Program Files).
2. Run Tbot Ultra.exe.
3. Add your account and server inside the app (Account -> Manage), then log in.

You do NOT need to edit .env or config files by hand - the app writes them for you
when you save your account and settings.

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
- Chromium is bundled in the portable folder.
- The captcha solver runtime is included in both downloads.
- Captcha auto-solve is disabled in the release template until you enable it in config.
