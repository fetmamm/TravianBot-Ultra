# ADR: Shutdown, bakgrundsjobb och cleanup

## Status

Aktivt beslut, 2026-06-08.

## Beslut

- Shutdown ar asynkron och blockerar inte WPF UI-traden med `Task.Wait`.
- Timers, loopar och sparade bakgrundsjobb stoppas och invantas fore browser-dispose.
- `LoopController` ager MainWindows CTS-/loop-state.
- Browser context, browser och Playwright stadas oberoende sa ett fel inte hoppar over resten.
- Delvis skapade browserresurser stadas vid initieringsfel.
- Modeless popups och externa captcha-processer avbryts och stangs.
- Efter cleanup avslutas applikationen explicit via `Application.Shutdown()`.

## Konsekvenser

Nya fristaende fire-and-forget-jobb eller CTS-falt i `MainWindow` ar inte tillatna. Anvand
befintlig task-tracking och `LoopController`. Detaljer finns i historikarkivet.
