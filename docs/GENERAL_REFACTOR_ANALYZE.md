# Projektanalys och refaktoriseringsplan

Analysera hela projektet och identifiera de 10 viktigaste förbättringarna/refaktoriseringarna.

Målet är att hitta stegvisa förbättringar med hög reward och låg risk, inte att skriva om systemet.

Analysera bland annat:

- Arkitektur och projektstruktur
- Ansvarsfördelning mellan lager
- Stora filer, klasser och metoder
- UI, ViewModels och Services
- State-hantering
- Queue-, scheduler- och loop-logik
- Async, threading och shutdown
- Duplicerad logik
- Testbarhet och felsökbarhet
- Kod som är svår att förstå, underhålla eller vidareutveckla

Regler:

- Basera analysen på faktisk kod.
- Föreslå inte omskrivning av hela system.
- Prioritera små och isolerade förbättringar.
- Var konkret med filer, klasser och metoder.
- Undvik generella rekommendationer.
- Ta hänsyn till ENGINEERING_NOTES.md och AGENTS.md.
- Om du inte kan peka ut specifik kod ska rekommendationen inte tas med.

Prioritera efter:

1. Risk/reward
2. Underhållsbarhet
3. Tydligare ansvarsfördelning
4. Lägre komplexitet
5. Mindre risk för regressioner
6. Enklare framtida AI-assisterad utveckling

Fokusera särskilt på:

- Filstorlek
- För många ansvar i samma klass
- UI-logik i code-behind
- Race conditions
- UI-freezes
- Shutdown/resource cleanup
- Otydliga gränser mellan UI, state, services och worker

Returnera endast:

# Top 10 rekommenderade åtgärder

| Rank | Område | Berörda filer/klasser | Problem | Föreslagen åtgärd | Risk | Reward | Storlek |
|------|---------|---------|---------|---------|---------|---------|---------|

Max 10 rader.

Avsluta med:

Rekommenderad start: ...

Lämna orört just nu: ...

Test efter första steg: ...