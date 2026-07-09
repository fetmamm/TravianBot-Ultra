# Proxy library

Status: implemented.

## Context
Tidigare lagrades proxy per konto som en fri sträng (`TBOT_{namn}_PROXY_SERVER` i `.env`) och host/port matades in manuellt i kontoeditorn. Det fanns ingen sparad lista med namngivna proxys, ingen översikt över vilka konton en proxy använts på, och inget skydd mot att råka köra samma proxy på flera konton (vilket kan länka kontona).

Målet: en **namngiven proxylista** som går att välja ur i kontoeditorn, som visar vilka konton varje proxy använts på, som **varnar** vid återanvändning och **valfritt kan låsa** en proxy till ett konto, samt en **"Add"-knapp** i proxy-scannen så top-resultat snabbt kan sparas till listan och redigeras. Enkelt att använda, tar inte bort befintlig funktion (manuell inmatning behålls).

Beslut: **Varna + valfri låsning.** **Dropdown + behåll manuella fält.**

## Datamodell & lagring
Global JSON-store som speglar `ProxyFinderStateStore` (`AtomicFile` + read-retry + `ProjectRootLocator` + `config/*.json`).

`src/TbotUltra.Desktop/Services/ProxyLibraryStore.cs` — fil `config/proxies.json`.
- DTO `ProxyLibraryEntry`: `Id`, `Name`, `Scheme`/`Host`/`Port` (`Server` = `scheme://host:port`), `Country`, `LatencyMs`, `AssignedAccount` (nullbar — valfri låsning), `UsedByAccounts` (`List<string>`, historik), `CreatedAtUtc`.
- Rena, testbara hjälpmetoder: `Upsert` (dedupe på `Server`), `FindByServer`, `AddUsage`, `ClassifyReuse` → `Ok` / `UsedByOthers` / `LockedToOther`.
- Koppling konto↔bibliotekspost via `Server`-matchning (ingen `.env`-schemaändring).
- Återanvänder `ProxyParser.MaskForLog` och samma host/port-parsning som `ProxyListTester` (`ProxyCandidate`).

## Proxy library-fönster
`src/TbotUltra.Desktop/ProxyLibraryWindow.xaml(.cs)` — speglar `ServerListWindow` (working-copy, snapshot-baserad unsaved-changes-guard, `AppDialog.ShowCustom`, Add/Save/Close).
- Konstruktor tar `ProxyLibraryStore` + kontonamn (från `EnvAccountStore.ListAccounts()`) för assignment-dropdownen.
- `DataGrid`-kolumner: **Name** (editerbar), **Proxy** (`host:port`) + **Type**, **Country**, **Assigned to** (dropdown av konton, tomt = olåst), **Used by** (read-only), **Delete**.

## Kontoeditor-integration (`AccountsWindow`)
- **"Saved proxies"-dropdown** + **"Proxy list…"-knapp** i proxy-sektionen. Manuella fält (`ProxySchemeComboBox`/`ProxyHostTextBox`/`ProxyPortTextBox`) behålls.
- Val i dropdown → fyll schema/host/port, sätt `UseProxyCheckBox`, kör dirty-tracking.
- **Vid val och vid Save** körs `ClassifyReuse(server, currentAccountName)`:
  - `LockedToOther(x)` → **blockera** med alarm (`AppDialog`).
  - `UsedByOthers(x…)` → **varna** (Ja/Avbryt), går att fortsätta.
  - `Ok` → tyst.
  - Aktuellt kontonamn = `_editingOriginalName` (befintligt) annars `AccountKeyNormalizer.MakeKey(username, serverUrl)`.
- **På lyckad Save**: matchar proxyn en bibliotekspost → `AddUsage(entryId, account.Name)` och persistera `proxies.json`.

## Proxy finder-integration (`ProxyFinderWindow`)
- **"Add"-knapp per rad** i resultat-`DataGrid` → `ProxyLibraryStore.Upsert` med default-namn (`"{Country} {Host}"`, annars `"{Host}:{Port}"`), country/latens från raden. Idempotent (dedupe på `Server`).

## Filer
Nya: `Services/ProxyLibraryStore.cs`, `ProxyLibraryWindow.xaml(.cs)`, `TbotUltra.Desktop.Tests/ProxyLibraryStoreTests.cs`.
Ändrade: `AccountsWindow.xaml(.cs)`, `ProxyFinderWindow.xaml(.cs)`.

## Verifiering
- Enhetstester: `Upsert` dedupar på Server; `AddUsage` idempotent; `ClassifyReuse` returnerar `LockedToOther`/`UsedByOthers`/`Ok` korrekt.
- Bygg (appen kan vara öppen → DLL-lås): `dotnet build src/TbotUltra.Desktop/TbotUltra.Desktop.csproj -p:OutDir=<temp>`.
- Manuellt e2e: scan → "Add" → "Proxy list…", döp om + lås till konto A → i konto B: låst blockeras, oanvänd fungerar, använd-på-A varnar → "Used by" uppdateras efter Save → `config/proxies.json` persisterar över omstart.
