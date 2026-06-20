# ADR 2026-06-20 - Town Hall celebration automation

## Beslut

- Ny grupp/task: `TownHallCelebration = 9`, task `run_town_hall_celebration`.
- Town Hall ar per by (`gid 24`) och galler alla stammar.
- Celebration mode ar `small`/`big`: account-default i NPC/Trade, per-by override i Town Hall overview.
- Hero resources ar opt-in via `HeroResourceUseTownHall` och default `false`.
- Running celebration sparas per account/by i `town_hall_state.json` som `{ mode, endsAtUtc }`.
- Vid restart seedas en deferred runtime item till `endsAtUtc` sa dashboard-timern visas utan ny navigation.
- Big kraver Town Hall level 10; under level 10 loggas fallback och small startas.

## Selectorstatus

- Small-start path ar implementerad med scoped `.build_details`-logik: small-celebration-rad + `.act` action.
- Live-DOM verifiering pa Official och SS-Travi kunde inte goras i denna implementation och maste goras innan
  selektorn betraktas som bekraftad.
- Big-start-selector ar avsiktligt ej implementerad tills en Town Hall level 10 finns att verifiera mot.
