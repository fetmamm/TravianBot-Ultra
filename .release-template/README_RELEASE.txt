Tbot Ultra (portable)

No installation needed.
1. Extract the zip to a folder where you have write access (e.g. your Desktop, NOT Program Files).
2. Move or rename the extracted folder wherever you like - it stays self-contained.
3. Run Tbot Ultra.exe inside the folder.
4. Add your account and server inside the app (Account -> Manage), then log in.

You do NOT need to edit .env or config files by hand - the app writes them for you
when you save your account and settings.

Running multiple accounts at the same time:
- Extract a SEPARATE copy of the portable folder for each account
  (e.g. Tbot-account1\, Tbot-account2\).
- Each folder has its own config, account, browser session and logs, so they
  run fully isolated and can run simultaneously.
- Do NOT run two instances from the SAME folder - they would share config,
  logs and the browser session and conflict with each other.

Notes:
- Config files (.env, config\) live next to the exe in the app folder.
- Use a folder the app can write to (logs, queue data, config updates).
- If .env or config files already exist from an older copy, they are kept.
- Chromium and the Playwright driver are bundled in the portable folder.
