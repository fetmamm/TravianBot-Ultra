# ADR: Multi-village, konto-state och koer

## Status

Aktivt beslut, 2026-06-05.

## Beslut

- Konto-/byspecifika installningar sparas under `config/accounts/<account>/`.
- Kontobyte aterstaller UI och cache men bevarar varje kontos separata ko och settings.
- Queue-items ar konto-scopeade och target-village ska respekteras.
- All per-slot projektion, deduplicering och UI-fargning filtreras till vald by eller globala items.
- Village-switch anvander kanonisk `dorf1.php?newdid={id}` och verifierar inloggat lage.

## Konsekvenser

Slot-id ar inte globalt unika mellan byar. Kod far inte harleda byspecifikt state fran en
ofiltrerad kontoko. Detaljerade regressioner finns i historikarkivet.
