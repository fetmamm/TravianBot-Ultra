# ADR: Smithy-upgrade och trupptraning

## Status

Aktivt beslut, 2026-06-20. Detaljerna bakom de korta reglerna i
`ENGINEERING_NOTES.md` (sektion 5 "Byggnader och ko" + "Automation groups").

## Smithy troop-upgrade

- `gid 13` ar Smithy; det finns ingen separat Armoury pa `gid 12`.
- Official-knappen ar `button[value="Improve"]` med `onclick ... action=research&t=tN`
  (SS/legacy: "Upgrade"); matcha pa `action=research`/text, aldrig bara knapptext. Identifiera trupp via
  `img.unit.uNN` eller `t=tN`, inte radordning (oresearchade trupper saknar rad). Pagaende research lases
  ENBART fran `table.under_progress .timer`; radens `.inlineIcon.duration` ar byggtid, inte progress.
  "Research is already being conducted." = ko upptagen; "Exchange resources"/"Enough resources on" = resursbrist.
  "Smithy level too low" = truppen ar vid smithyns nivatak -> `SmithyTroopOutcome.SmithyLevelTooLow`, TERMINAL
  (skippas, inte defer). Utan detta hamnade trupp under mal men vid taket i InProgress-fallbacken och spammade
  tasken nar anvandarens malniva lag over smithyns niva.
- Valda trupper + malnivaer sparas konto-scopeat i `smithy_upgrade.json` och skickas som task-payload
  `smithy_upgrade_targets="u21=20;..."`; tom payload = no-op. Stateless tolkning i `SmithyPageParser` (Core, enhetstestad).
  Den kontinuerliga loopen injicerar payloaden per by (`MainWindow.ContinuousLoop`) — utan den blir loop-tasken no-op.
- Vanta in actionable sida med `WaitForPageReadyAsync` fore radlasning. Resursbrist: om `hero_resource_transfer_enabled`
  och Official klickas truppens egen `.inlineIcon.resource.transfer` och "Transfer selected" bekraftas endast nar Travian
  aktiverar den (hero racker); ett forsok per trupp/korning. Maste smithyn sjalv byggas och byggkon ar full -> defer pa
  `UpgradeBuildingToMaxAsync`-resultatets `queue_wait_seconds` (ingen idé att forsoka nar byggkon ar full).
- Plus ger TVA samtidiga research-slots (som andra byggkon). `UpgradeSelectedTroopsAtSmithyAsync` laser Plus
  via `GetCachedTribeAndPlusAsync` -> `maxConcurrentUpgrades = plus ? 2 : 1`, klickar en Improve per pass och
  laser om for att fylla nasta slot. Aktivt antal = `under_progress`-rader (`dashQueue.Count`). Defer pa
  research-kotimern ENDAST nar kon ar full (`activeCount >= maxConcurrentUpgrades`) eller en ledig slot inte
  kunde fyllas efter stall-retries; med ledig fyllbar slot vantas pa resurs-ETA/moderat omkoll i stallet for
  att inte lata slot 2 sta tom timmar. Efter ett klick kan React-omrendering doja nasta Improve-knapp -> upp
  till 3 reload-forsok (`consecutiveFreeSlotStallReads`) innan defer, sa racet inte lamnar slot 2 tom.
- Worker emitterar `[smithy-queue] entries_json=...` fran verkliga `under_progress`-rader. Desktop lagrar
  namn, malniva och absolut sluttid i byns `SmithyUpgradeStatus`; Queue-ruta, ikoner och loopkort laser
  samma SOT. Tom/glitch-lasning far inte radera en aktiv framtida ko; posten forsvinner nar sluttiden passeras.
- Inget kvar att gora: nar ALLA valda trupper ar terminala (at-target/maxed/smithy-too-low/not-researched)
  och inget forbattrades (`pending==0 && improved==0`) avslutar workern med `smithy_nothing_to_do=1`. Desktop
  (`DisableSmithyGroupIfNothingToDoAsync`, endast continuous-loop) stanger DA av Troops-gruppen for den byn
  (`PersistAutomationGroupEnabledForVillage`) sa loopen slutar koa om tasken varje varv. `upgrade_troops_at_smithy`
  ar enda tasken i Troops-gruppen, sa inget annat paverkas. Att valja trupper i upgrade-options-popupen (single
  eller sync, icke-tom) sl ar PA gruppen igen.

## Bygg trupper (Barracks/Stable/Workshop)

- Traningsformularet keyar mangd-inputen pa det tribe-relativa truppslotet `t1..t10`
  (Official: Ram = `t7`), INTE det globala unit-id:t (Ram = `u27`). Identifiera raden via unit-id-ikonen
  (`img.uNN`) och las radens verkliga input-namn — fungerar pa bada varianter. Anvand
  `TroopCatalog.ResolveTroopIndex` for slotet och `ResolveTravianUnitId` enbart for ikonidentifiering.
  Max-lanken: SS `tN.value=NN`, Official `...val(NN)`; matcha bada plus numerisk lanktext, scopeat till raden.
- Traningskon ar SOT for traningstimers, precis som construction: las kvarvarande sekunder ur
  `table.under_progress td.dur .timer` (`value`/`data-value` eller text), ankra som absolut `TimerSnapshot`
  pa servertid (`_serverTimeUtc`). `TroopTrainingQueueState.PreserveKnownActiveQueue` behaller en levande ko
  vid tom/partiell lasning; UI tickar ned via `Tick(serverNow)` och posten forsvinner forst nar sluttiden passeras.

## Per-by-traning och UI-synk

- Per-by-traning sparas konto-scopeat i `troop_training.json` (`TroopTrainingSettingsStore`, nyckel = bykoord).
  Loopen snapshotar override:n in i `build_troops`-payloaden; saknad override = global config (bakatkompatibelt).
- Troops-tabben (`TroopTrainingViewModel`) redigerar VALD bys override: load/bybyte laser byns payload via
  `ApplyTroopTrainingForSelectedVillage` (`ApplyVillageTrainingPayload`), och byggnadsregel-andringar sparas
  per by (`PersistTroopTrainingForSelectedVillage` -> `BuildVillageTrainingPayload`). Konto-vida falt (NPC
  trade, guld, brewery celebration) ligger kvar i `settings.json` (`WriteToConfig`/`PersistTroopTrainingConfig`)
  och skrivs INTE per by.
- "Training options"-popupen ar samma per-by-data i en byoversikt och bara enabled/troop-val; bada ytor synkar
  (popup-save -> reload tab; tab-edit -> popup laser override vid oppning). Truppdropdownen ar per byggnad:
  `TroopCatalog.ResolveTroopTypesForTribe(tribe, buildingType)` (Barracks far inte visa Stable/Workshop-trupper)
  — galler bade tab och popup. Popup-toggles anvander `ToggleSwitchBlueStyle`.

## Village settings: automation groups

- Village settings-popupens "Automation groups"-kolumn speglar dashboardens loop-kort per by och sparas i
  samma `VillageSettingsStore.EnabledGroups`; avbockad grupp slutar koras for byn (`IsGroupEnabledForVillage`).
- Popupens gruppkolumner har fast ordning oberoende av dashboardens dragordning:
  Hero, Construction, Upgrade Troops, Build Troops, Farming, Brewery, NPC, Resource Transfer, Reinforcements.
- Nya byar far explicit `Construction=true`, ovriga grupper och NPC=false. Auto=true endast nar den forsta
  inlasningen innehaller exakt en by; senare nya byar far Auto=false. `Save & close` vacker en pagaende loop.

## Konsekvenser

Smithy- och traningstimers ar browserbekraftade SOT (`SmithyUpgradeStatus` / traningskon), inte lokala
gissningar. Truppidentitet sker pa unit-id-ikon, mangd/max pa tribe-relativt slot. All stateless tolkning
ligger i Core-parsers med enhetstester. Full bakgrund finns i `docs/history/engineering-notes-archive.md`.
