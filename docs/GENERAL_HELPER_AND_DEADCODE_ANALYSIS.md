# Oanvänd kod och helper-analys

Analysera hela lösningen och identifiera kod som sannolikt kan tas bort, förenklas eller refaktoriseras.

Fokusera på:

- Private methods med 0 referenser
- Oanvända helper-metoder
- Dead code
- Oanvända utility-klasser
- Oanvända services
- Oanvända fält
- Oanvända properties
- Oanvända ViewModels
- Oanvända Commands
- Duplicerade helpers
- Helpers som endast används en gång
- Helpers som ligger i fel klass
- Logik som borde vara helper men idag är duplicerad

Regler:

- Modifiera inte kod.
- Basera analysen på faktisk kod.
- Gissa inte.
- Rapportera endast sådant som kan verifieras.
- Ta hänsyn till ENGINEERING_NOTES.md och AGENTS.md.
- Om en metod används via reflection, DI, event binding, XAML binding, serialization eller liknande ska den markeras som "kräver manuell verifiering" istället för oanvänd.
- Prioritera säkra fynd med hög sannolikhet.

Prioritera:

1. Oanvänd kod som kan tas bort direkt
2. Oanvända helpers
3. Duplicerade helpers
4. Helpers som endast används en gång
5. Kandidater för ny helper-extraktion

Returnera endast:

# Rapport

| Typ | Fil/Klass | Metod/Fält/Property | Problem | Rekommenderad åtgärd | Säkerhet |
|------|------|------|------|------|------|

Säkerhet:

- Hög = verifierbart oanvänd
- Medium = sannolikt oanvänd
- Låg = kräver manuell verifiering

Gruppera resultat per fil där det är möjligt.

Avsluta med:

Kod som sannolikt kan tas bort direkt: ...

Största helper-städningen: ...

Kräver manuell verifiering: ...