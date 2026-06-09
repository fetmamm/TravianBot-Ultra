# ADR: Dashboard overview och account-scoped settings

## Status

Aktivt beslut, 2026-06-06.

## Beslut

- Dashboarden anvander befintligt `MainWindow.Dashboard.Settings`-monster.
- Seedning av checkboxar skyddas av suppress-flagga sa config inte skrivs under load.
- Nya dashboard-settings ar konto-scopeade om de styr kontoautomation.
- Periodiska automationstriggers anvander befintlig refresh-loop och deduplicerar mot aktiv ko.
- Village overview sammanfor oberoende statuskallor konservativt och skriver inte over bra cache med tom data.

## Konsekvenser

En ny setting maste kopplas genom hela configkedjan och fa en info-tooltip. Langa listor
ska scrolla utan att trycka undan andra dashboardsektioner.
