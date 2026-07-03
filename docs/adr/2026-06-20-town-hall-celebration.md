# ADR 2026-06-20 - Town Hall celebration automation

## Beslut

- Ny grupp/task: `TownHallCelebration = 9`, task `run_town_hall_celebration`.
- Town Hall ar per by (`gid 24`) och galler alla stammar.
- Celebration mode ar `small`/`big`: account-default i NPC/Trade, per-by override i Town Hall overview. UI visar `big` som "Great" enligt Travian.
- Hero resources ar opt-in via `HeroResourceUseTownHall` och default `false`.
- Running celebration sparas per account/by i `town_hall_state.json` som `{ mode, endsAtUtc }`.
- Vid restart seedas en deferred runtime item till `endsAtUtc` sa dashboard-timern visas utan ny navigation.
- Big/Great kraver Town Hall level 10; under level 10 loggas fallback och small startas.

## Selectorstatus

- Start path ar implementerad med scoped `.build_details`-logik: small- eller Great-celebration-rad + `.cta`/`td.act` action.
- Great-selector ar verifierad mot sparad Official DOM fran Town Hall level 10: raden heter "Great celebration".
