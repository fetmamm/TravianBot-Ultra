# ADR: Multi-village, konto-state och koer

## Status

Aktivt beslut, 2026-06-05.

## Beslut

- Konto-/byspecifika installningar sparas under `config/accounts/<account>/`.
- Kontobyte aterstaller UI och cache men bevarar varje kontos separata ko och settings.
- Queue-items ar konto-scopeade och target-village ska respekteras.
- Queue-items barer den stabila koordinatnyckeln (`target_village_key`) for by-identitet, inte bara namn:
  namnbaserad uppslagning kraschar nar en by forloras och atergrundas med samma namn (resolvas till fel by ->
  posterna pausas/gating:as bort). Aldre poster utan nyckel faller tillbaka pa namn/url.
- Forlorade/forstorda byar rensas retention-baserat (`LostVillageRetention`, 3 dygn confirmed-missing pa
  koordinatidentitet): bade settings-posten och dess kvarvarande koposter tas bort, sa ingen skrapkö ligger
  kvar och en atergrundad by med samma namn blir entydig.
- All per-slot projektion, deduplicering och UI-fargning filtreras till vald by eller globala items.
- Village-switch anvander kanonisk `dorf1.php?newdid={id}` och verifierar inloggat lage.

## Konsekvenser

Slot-id ar inte globalt unika mellan byar. Kod far inte harleda byspecifikt state fran en
ofiltrerad kontoko. Detaljerade regressioner finns i historikarkivet.
