# Proxy library — plan

## Context
Idag lagras proxy per konto som en fri sträng (`TBOT_{namn}_PROXY_SERVER` i `.env`) och man matar in host/port manuellt i kontoeditorn. Det finns ingen sparad lista med namngivna proxys, ingen översikt över vilka konton en proxy använts på, och inget skydd mot att råka köra samma proxy på flera konton (vilket kan länka kontona).

Målet: en **namngiven proxylista** som går att välja ur i kontoeditorn, som visar vilka konton varje proxy använts på, som **varnar** vid återanvändning och **valfritt kan låsa** en proxy till ett konto, samt en **"Add"-knapp** i proxy-scannen så top-10-resultat snabbt kan sparas till listan och redigeras. Ska vara enkelt och inte ta bort befintlig funktion (manuell inmatning behålls).

Beslut (bekräftat med användaren): **Varna + valfri låsning**. **Dropdown + behåll manuella fält.**

## Datamodell & lagring (nytt)

Ny global JSON-store som speglar [ProxyFinderStateStore.cs](src/TbotUltra.Desktop/Services/ProxyFinderStateStore.cs) (samma `AtomicFile` + read-retry + `ProjectRootLocator` + `config/*.json`-mönster).

**`src/TbotUltra.Desktop/Services/ProxyLibraryStore.cs`** — fil `config/proxies.json`.
- DTO `ProxyLibraryEntry`:
  - `Id` (guid-sträng, stabil)
  - `Name` (användarens namn)
  - `Scheme`, `Host`, `Port` (kanonisk form; `Server` = `scheme://host:port`)
  - `Country`, `LatencyMs` (valfria, fylls från scan-enrichment)
  - `AssignedAccount` (nullbar account-`Name` — den valfria låsningen)
  - `UsedByAccounts` (`List<string>` av account-`Name`, historik)
  - `CreatedAtUtc`
- Metoder: `Load()` (returnerar lista, tom om saknas/korrupt), `Save(list)`, samt små **rena, testbara** hjälpmetoder:
  - `Upsert(entry)` — dedupe på `Server` (case-insensitivt); uppdaterar country/latency om den redan finns, annars lägg till. Returnerar entryt.
  - `FindByServer(server)` — matcha en proxy-sträng mot listan (via host:port, schema-normaliserat).
  - `AddUsage(entryId, accountName)` — lägg till konto i `UsedByAccounts` om ej redan med.
  - `ClassifyReuse(server, currentAccountName)` → enum `ProxyReuse { Ok, UsedByOthers, LockedToOther }` + ev. kontonamn. **Ren funktion → enhetstestas.**

Ingen ändring av `.env`-schemat: kopplingen konto↔bibliotekspost är implicit via `Server`-matchning (biblioteket dedupas på `Server`). Återanvänder [ProxyParser.MaskForLog](src/TbotUltra.Worker/Infrastructure/ProxyParser.cs) för loggning och samma host/port-parsning som `ProxyFinderWindow`/`ProxyListTester` (`ProxyCandidate`).

## Proxy library-fönster (nytt)

**`src/TbotUltra.Desktop/ProxyLibraryWindow.xaml(.cs)`** — speglar [ServerListWindow](src/TbotUltra.Desktop/ServerListWindow.xaml.cs) (working-copy, snapshot-baserad unsaved-changes-guard, `AppDialog.ShowCustom` vid stängning, Add/Save/Close).
- Konstruktor tar `ProxyLibraryStore` + kontonamn-lista (från [EnvAccountStore.ListAccounts()](src/TbotUltra.Desktop/Services/EnvAccountStore.cs)) för assignment-dropdownen.
- `DataGrid`-kolumner:
  - **Name** (editerbar, `UpdateSourceTrigger=PropertyChanged`)
  - **Proxy** (`host:port`, read-only) + **Type** (schema)
  - **Country** (read-only)
  - **Assigned to** (editerbar via `DataGridComboBoxColumn` med kontonamn + tomt = olåst)
  - **Used by** (read-only, komma-separerad lista)
  - **Delete**-knapp (per rad, som `DeleteRowButton_Click`)
- Save validerar och skriver via `ProxyLibraryStore.Save`. DTO görs till `INotifyPropertyChanged` (som [ServerOption](src/TbotUltra.Desktop/Models/ServerOption.cs)) för inline-edit.

## Kontoeditor-integration ([AccountsWindow](src/TbotUltra.Desktop/AccountsWindow.xaml))

Proxy-sektionen (Row 4) — lägg till en rad ovanför/under type/host/port-nätet:
- **"Saved proxies"-dropdown** (`ComboBox`) som listar biblioteksposter (visar `Name — host:port`, med en markör om använd/låst).
- **"Proxy list…"-knapp** som öppnar `ProxyLibraryWindow` (mönster som `ServerListButton_Click`, reload efter stängning).
- Manuella fält (`ProxySchemeComboBox`/`ProxyHostTextBox`/`ProxyPortTextBox`) **behålls**.

Beteende:
- Väljer man en proxy i dropdownen → fyll schema/host/port via befintliga `SelectProxyScheme` + textfält, sätt `UseProxyCheckBox`, kör dirty-tracking (`EditorField_Changed`/`UpdateActionButtons`).
- **Vid val OCH vid Save** körs `ClassifyReuse(server, currentAccountName)`:
  - `LockedToOther(x)` → **blockera** (alarm via [AppDialog.Show](src/TbotUltra.Desktop/AppDialog.xaml.cs), Warning): "Proxyn är låst till konto 'x'." — vid val: fyll inte; vid Save: avbryt.
  - `UsedByOthers(x…)` → **varna** (`AppDialog.ShowCustom` Ja/Avbryt): "Har använts på 'x'. Använd ändå?" — Ja fortsätter.
  - `Ok` → tyst.
  - Aktuellt kontonamn = `_editingOriginalName` vid befintligt konto, annars `AccountKeyNormalizer.MakeKey(username, serverUrl)` (kan vara nytt/tomt → behandla som "inget eget konto än").
- **På lyckad Save** (efter `_store.SaveAccount`): om proxyn matchar en bibliotekspost → `AddUsage(entryId, account.Name)` och persistera `proxies.json`.

## Proxy finder-integration ([ProxyFinderWindow](src/TbotUltra.Desktop/ProxyFinderWindow.xaml))

- Lägg till en **"Add"-knapp per rad** i resultat-`DataGrid` (bredvid "Use"), handler `AddToLibraryButton_Click`:
  - `ProxyLibraryStore.Upsert` med default-namn (`"{Country} {Host}"`, annars `"{Host}:{Port}"`), sätt Country/LatencyMs från raden.
  - Idempotent (dedupe på `Server`); visa kort bekräftelse i `ValidationTextBlock` ("Added / already in list").
- `ProxyResultRow` har redan `Candidate` (schema/host/port) + `LatencyMs` + `Country` → allt som behövs.

## Filer

**Nya:**
- `src/TbotUltra.Desktop/Services/ProxyLibraryStore.cs` (store + DTO + rena hjälpmetoder)
- `src/TbotUltra.Desktop/ProxyLibraryWindow.xaml` + `.xaml.cs`
- `src/TbotUltra.Desktop.Tests/ProxyLibraryStoreTests.cs` (dedupe/`Upsert`, `AddUsage`, `ClassifyReuse` för Ok/UsedByOthers/LockedToOther)

**Ändrade:**
- `AccountsWindow.xaml` / `.xaml.cs` — dropdown + "Proxy list…"-knapp, val-/save-hantering, reuse/lock-kontroll
- `ProxyFinderWindow.xaml` / `.xaml.cs` — "Add"-knapp per rad + handler
- Ev. `AccountsWindow`-konstruktor/`MainWindow.Session.cs` om store behöver injiceras (annars instansieras `ProxyLibraryStore` fristående via `ProjectRootLocator`, som `ProxyFinderStateStore` — föredraget för minsta ändring)

## Verifiering
- **Enhetstester** (`ProxyLibraryStoreTests`): `Upsert` dedupar på Server; `AddUsage` idempotent; `ClassifyReuse` returnerar `LockedToOther` när `AssignedAccount` är annat konto, `UsedByOthers` när `UsedByAccounts` innehåller annat konto, `Ok` för samma/nytt konto och olåst/oanvänd. Kör: `dotnet test src/TbotUltra.Desktop.Tests` (eller `Worker.Tests`-mönstret om logiken läggs i Worker).
- **Bygg** (appen kan vara öppen → DLL-lås): `dotnet build src/TbotUltra.Desktop/TbotUltra.Desktop.csproj -p:OutDir=<temp>`.
- **Manuellt end-to-end:** kör en scan → "Add" på topp-3 → öppna "Proxy list…", döp om + lås en till konto A → i konto B välj den låsta (ska blockeras med alarm) och välj en oanvänd (ska fungera) → välj en som använts på A utan lås (ska varna men gå att fortsätta) → verifiera att "Used by" uppdateras efter Save och att `config/proxies.json` persisterar över omstart.
