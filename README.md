# Tbot_ultra_new
Travian bot project for automating tasks on a private server.

## C# Desktop App (Primary)
- UI: `src/TbotUltra.Desktop`
- Engine: `src/TbotUltra.Worker`
- Target framework: `.NET 8`

### Start the app
- Double-click `Start_Tbot.bat`
- or run: `dotnet run --project src/TbotUltra.Desktop/TbotUltra.Desktop.csproj -c Debug`

### Configuration
The app reads:
- `config/bot.json`
- `config/queue.json`
- `.env`

Example task names in `loop_tasks`:
- `status`
- `scan_all_villages`
- `account_snapshot`
- `upgrade_resource_to_level`
- `upgrade_resource_to_max`
- `upgrade_all_resources_to_level`
- `upgrade_building_to_level`
- `upgrade_building_to_max`
- `construct_building`

Additional task parameters are also read from `config/bot.json` (resource/building fields).

## Status
- Runtime and UI are fully C# (`TbotUltra.Desktop` + `TbotUltra.Worker`).
- Root-level Python launch/dependency files are removed.
- Queue system is persisted in `config/queue.json` and managed from the Queue tab in the desktop UI.

## Smoke check
- Run `Smoke_Check.bat`
- or run:
  - `dotnet build TbotUltra.sln -c Debug`
  - `dotnet test src/TbotUltra.Worker.Tests/TbotUltra.Worker.Tests.csproj -c Debug`
