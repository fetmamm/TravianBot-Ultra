# Robusthetsanalys

Analysera projektet och identifiera de 10 största riskerna för:

- Programkrasch
- Deadlock
- UI-freeze
- Dataförlust
- Race conditions
- Resursläckor
- Oändliga loopar
- Felaktig automation
- Korrupt state
- Felaktig konto-/byhantering

Regler:

- Analysera faktisk kod.
- Rapportera endast problem som kan kopplas till specifika filer, klasser eller metoder.
- Undvik generella rekommendationer.
- Ingen kod.
- Ingen omskrivning av hela system.
- Prioritera hög risk och hög sannolikhet.

Returnera:

| Rank | Område | Filer/Klasser | Problem | Föreslagen lösning | Sannolikhet | Konsekvens | Risk |
|------|---------|---------|---------|---------|---------|---------|---------|

Max 10 rader.

Avsluta med:

Största risk just nu: ...

Bäst risk/reward-fix: ...

Kan vänta: ...