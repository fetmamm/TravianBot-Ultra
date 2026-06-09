# ADR: ServerFlavor och gemensam kodbas

## Status

Aktivt beslut, 2026-06-01.

## Beslut

- Official och SS-Travi stods i samma kodbas.
- `ServerFlavor` ar en computed property fran `BaseUrl`-host.
- `*.ss-travi.com` ger `SsTravi`; ovriga hosts ger `Official`.
- Flavor binds inte fran config och cachas inte separat.
- Official-selektorer laggs till additivt; fungerande SS-fallbacks behalls.
- Skillnader i URL kapslas i flavor-aware path helpers.
- Privatserverfunktioner gate:as med `_config.IsPrivateServer`.

## Konsekvenser

En fork eller stor `IServerAdapter`-omskrivning ska inte introduceras. React-sidor pa Official
kraver render-vantan och live-verifiering. Full historik finns i
`docs/history/engineering-notes-archive.md`.
