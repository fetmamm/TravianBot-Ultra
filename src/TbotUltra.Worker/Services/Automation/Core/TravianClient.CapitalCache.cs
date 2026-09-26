using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private readonly object _capitalCacheSync = new();
    private readonly Dictionary<string, CapitalCacheEntry> _capitalCacheByKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _capitalCacheLoaded;

    private static readonly JsonSerializerOptions CapitalCacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private bool IsCapitalProfileVerificationDue()
        => _session.CapitalProfileVerificationRequired
            && DateTimeOffset.UtcNow >= _session.CapitalProfileVerificationNotBeforeUtc;

    private void RequireCapitalProfileVerification(string reason, bool delayRetry)
    {
        _session.CapitalProfileVerificationRequired = true;
        _session.CapitalProfileVerificationNotBeforeUtc = delayRetry
            ? DateTimeOffset.UtcNow.AddMinutes(20)
            : DateTimeOffset.MinValue;
        if (_session.LogValueChanged("capital:profile-verification", reason))
        {
            Notify($"[capital] profile verification required: {reason}");
        }
    }

    private void ConfirmCapitalProfileVerification()
    {
        _session.CapitalProfileVerificationRequired = false;
        _session.CapitalProfileVerificationNotBeforeUtc = DateTimeOffset.MinValue;
    }

    public async Task<CapitalProfileCheckResult> CheckCapitalFromProfileAsync(
        CancellationToken cancellationToken = default)
    {
        Notify("[capital] checking player profile for the verified capital.");
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            await EnsureLoggedInAsync(cancellationToken: cancellationToken);
            try
            {
                await _page.WaitForSelectorAsync(
                    "#playerProfile table.villages tbody tr",
                    new PageWaitForSelectorOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 5000,
                    }).WaitAsync(cancellationToken);
            }
            catch (TimeoutException)
            {
                Notify("[capital:verbose] profile village table did not render within 5s.");
                throw new InvalidOperationException(
                    "Player profile village table did not finish rendering. No capital state was changed.");
            }

            var capitals = await _page.EvaluateAsync<PlayerProfileVillageRowJs[]>(
                """
                () => {
                  const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
                  const parseCoordinate = (value, axis) => {
                    const source = clean(value)
                      .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                      .replace(/[−–—]/g, '-');
                    const query = source.match(new RegExp(`[?&]${axis}=(-?\\d+)`, 'i'));
                    if (query) return Number.parseInt(query[1], 10);
                    const match = source.match(/-?\d+/);
                    return match ? Number.parseInt(match[0], 10) : null;
                  };
                  const capitals = [];
                  for (const row of document.querySelectorAll('table tr')) {
                    const isCapital = Array.from(row.querySelectorAll('span.additionalInfo'))
                      .some(node => /\bcapital\b/i.test(node.textContent || ''));
                    if (!isCapital) continue;

                    const name = clean(
                      row.querySelector('td.name a, td.village a, td.name, td.village, td:first-child')?.textContent || '')
                      .replace(/\s*\(capital\)\s*/i, '')
                      .trim();
                    const coordAnchor = row.querySelector('td.coordinates a[href*="x="][href*="y="], a[href*="karte.php?x="]');
                    const coordHref = coordAnchor?.getAttribute('href') || '';
                    const x = parseCoordinate(coordHref, 'x')
                      ?? parseCoordinate(row.querySelector('td.coordinates .coordinateX, .coordinateX')?.textContent || '', 'x');
                    const y = parseCoordinate(coordHref, 'y')
                      ?? parseCoordinate(row.querySelector('td.coordinates .coordinateY, .coordinateY')?.textContent || '', 'y');
                    if (!name || x === null || y === null) continue;
                    capitals.push({ name, isCapital: true, x, y });
                  }
                  return capitals;
                }
                """);

            if (capitals is not { Length: 1 })
            {
                var count = capitals?.Length ?? 0;
                Notify($"[capital:verbose] profile check incomplete: expected exactly one capital row, found {count}.");
                throw new InvalidOperationException(
                    $"Player profile did not identify exactly one capital village (found {count}). No capital state was changed.");
            }

            var capitalRow = capitals[0];
            if (string.IsNullOrWhiteSpace(capitalRow.Name) || !capitalRow.X.HasValue || !capitalRow.Y.HasValue)
            {
                throw new InvalidOperationException(
                    "Player profile capital row was missing its village name or coordinates. No capital state was changed.");
            }

            var capital = new CapitalProfileCheckResult(capitalRow.Name, capitalRow.X.Value, capitalRow.Y.Value);
            Notify($"[capital] profile check identified '{capital.VillageName}' at {capital.CoordX}|{capital.CoordY}; awaiting confirmation.");
            return capital;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl)
                && !string.Equals(previousUrl, _page.Url, StringComparison.OrdinalIgnoreCase))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }
    }

    public async Task SetVerifiedCapitalStateAsync(
        CapitalProfileCheckResult capital,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capital.VillageName);
        cancellationToken.ThrowIfCancellationRequested();

        var cachedVillages = _cachedVillages;
        var matchingVillageCount = cachedVillages?.Count(village =>
            village.CoordX == capital.CoordX && village.CoordY == capital.CoordY) ?? 0;
        if (matchingVillageCount != 1)
        {
            Notify($"[capital] shared village cache did not contain verified capital '{capital.VillageName}'; refreshing the player profile before applying state.");
            cachedVillages = (await ReadVillagesFromServerAsync(cancellationToken)).ToList();
            matchingVillageCount = cachedVillages.Count(village =>
                village.CoordX == capital.CoordX && village.CoordY == capital.CoordY);
        }

        if (matchingVillageCount != 1)
        {
            throw new InvalidOperationException(
                $"Verified capital '{capital.VillageName}' ({capital.CoordX}|{capital.CoordY}) was not found exactly once in the live village list. No capital state was changed.");
        }

        SaveCachedVillageState(capital.VillageName, true, capital.CoordX, capital.CoordY);
        UpdateCachedVillages(CapitalStateResolver.ApplyVerifiedCapital(
            cachedVillages!,
            capital.CoordX,
            capital.CoordY));
        Notify($"[capital] verified capital state set to '{capital.VillageName}' at {capital.CoordX}|{capital.CoordY}.");
        await TryEmitUiSyncSnapshotAsync(cancellationToken, force: true);
    }

    private (IReadOnlyList<Village> Villages, bool? IsCapital) ApplyCapitalEvidenceFromResourceFields(
        string activeVillage,
        (int? X, int? Y) activeCoords,
        IReadOnlyList<ResourceField> resourceFields,
        IReadOnlyList<Village> villages)
    {
        var cachedIsCapital = TryGetCachedCapitalState(activeVillage, activeCoords.X, activeCoords.Y);
        if (!resourceFields.Any(field => field.Level > 10))
        {
            return (villages, cachedIsCapital);
        }

        if (!activeCoords.X.HasValue || !activeCoords.Y.HasValue)
        {
            Notify($"[capital] resource field above level 10 in '{activeVillage}', but active village coordinates were unavailable; capital state was not changed.");
            return (villages, cachedIsCapital);
        }

        var villageListAlreadyResolved = villages.Count(village => village.IsCapital == true) == 1
            && villages.Any(village => village.IsCapital == true
                && village.CoordX == activeCoords.X
                && village.CoordY == activeCoords.Y);
        if (cachedIsCapital == true && villageListAlreadyResolved)
        {
            return (villages, true);
        }

        if (cachedIsCapital != true)
        {
            SaveCachedVillageState(activeVillage, true, activeCoords.X, activeCoords.Y);
        }

        var updatedVillages = CapitalStateResolver.ApplyDefinitiveResourceFieldEvidence(
            villages,
            resourceFields,
            activeCoords.X.Value,
            activeCoords.Y.Value);
        if (!ReferenceEquals(updatedVillages, villages))
        {
            UpdateCachedVillages(updatedVillages);
        }
        ConfirmCapitalProfileVerification();

        if (cachedIsCapital != true)
        {
            var highestLevel = resourceFields.Max(field => field.Level ?? 0);
            Notify($"[capital] '{activeVillage}' ({activeCoords.X}|{activeCoords.Y}) set as capital from live Dorf1 resource field level {highestLevel}.");
        }

        return (updatedVillages, true);
    }

    private async Task<bool?> ReadIsCapitalAsync(
        string villageName,
        int? coordX,
        int? coordY,
        CancellationToken cancellationToken)
    {
        Notify("[ReadIsCapitalAsync] started for village.");
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            var result = await _page.EvaluateAsync<string>(
                """
                (target) => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const wanted = clean(target.name).toLowerCase();
                  if (!wanted) return 'unknown';

                  // Official Travian (T4.6) adds span.additionalInfo with the text "(Capital)"
                  // inside the village's td.name cell.
                  let capitalSpan = null;
                  for (const info of document.querySelectorAll('td.name span.additionalInfo, span.additionalInfo')) {
                    if (/\bcapital\b/i.test(info.textContent || '')) {
                      capitalSpan = info;
                      break;
                    }
                  }
                  if (!capitalSpan) return 'unknown';

                  // Confine the comparison to the row that contains the capital marker.
                  // Walking further up reaches the table body which holds *all* village names
                  // and would falsely report any village as capital.
                  const row = capitalSpan.closest('tr, li, .row, .villageRow, .entry');
                  if (!row) return 'unknown';

                  const nameCell = row.querySelector('td.name, td.village, td:first-child');
                  const rowText = clean((nameCell || row).textContent || '').toLowerCase();
                  if (!rowText.includes(wanted)) return 'false';

                  if (Number.isInteger(target.x) && Number.isInteger(target.y)) {
                    const parseCoordinate = (value, axis) => {
                      const source = clean(value)
                        .replace(/[\u200e\u200f\u202a-\u202e\u2066-\u2069]/g, '')
                        .replace(/[−–—]/g, '-');
                      const query = source.match(new RegExp(`[?&]${axis}=(-?\\d+)`, 'i'));
                      if (query) return Number.parseInt(query[1], 10);
                      const match = source.match(/-?\d+/);
                      return match ? Number.parseInt(match[0], 10) : null;
                    };
                    const coordAnchor = row.querySelector('td.coordinates a[href*="x="][href*="y="], a[href*="karte.php?x="]');
                    const coordHref = coordAnchor?.getAttribute('href') || '';
                    const rowX = parseCoordinate(coordHref, 'x')
                      ?? parseCoordinate(row.querySelector('.coordinateX, .coordinate.x')?.textContent || '', 'x');
                    const rowY = parseCoordinate(coordHref, 'y')
                      ?? parseCoordinate(row.querySelector('.coordinateY, .coordinate.y')?.textContent || '', 'y');
                    if (rowX === null || rowY === null) return 'unknown';
                    return rowX === target.x && rowY === target.y ? 'true' : 'false';
                  }

                  return 'true';
                }
                """,
                new { name = villageName, x = coordX, y = coordY });

            return result?.ToLowerInvariant() switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousUrl))
            {
                await GotoAsync(previousUrl, cancellationToken);
            }
        }
    }

    private async Task RefreshCapitalStateForActiveVillageAsync(CancellationToken cancellationToken)
    {
        Notify("[RefreshCapitalStateForActiveVillageAsync] started");
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(activeVillage))
        {
            Notify("[RefreshCapitalStateForActiveVillageAsync] could not determine active village name, skipping capital state refresh.");
            return;
        }

        var activeCoords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var isCapital = await ReadIsCapitalAsync(activeVillage, activeCoords.X, activeCoords.Y, cancellationToken);
        SaveCachedVillageState(activeVillage, isCapital, activeCoords.X, activeCoords.Y);
    }

    private async Task<string?> TryReadActiveVillageNameSafeAsync(CancellationToken cancellationToken)
    {
        Notify("[scan:verbose] reading active village name from page");
        try
        {
            return await ReadActiveVillageNameAsync(cancellationToken);
        }
        catch
        {
            Notify("[scan:verbose] failed to read active village name from page");
            return null;
        }
    }

    private bool? TryGetCachedCapitalState(string villageName)
        => TryGetCachedCapitalState(villageName, null, null);

    private bool? TryGetCachedCapitalState(string villageName, int? coordX, int? coordY)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return null;
        }

        EnsureCapitalCacheLoaded();
        lock (_capitalCacheSync)
        {
            var capitalCount = _capitalCacheByKey.Values.Count(entry =>
                IsCurrentAccountServer(entry) && entry.IsCapital == true);
            if (capitalCount > 1)
            {
                RequireCapitalProfileVerification(
                    $"capital cache contains {capitalCount} candidates for this account/server",
                    delayRetry: false);
                return null;
            }

            if (coordX.HasValue && coordY.HasValue)
            {
                var coordinateKey = BuildCapitalCacheKey(villageName, coordX, coordY);
                return _capitalCacheByKey.TryGetValue(coordinateKey, out var coordinateEntry)
                    ? coordinateEntry.IsCapital
                    : null;
            }

            var matches = _capitalCacheByKey.Values
                .Where(entry => IsCurrentAccountServer(entry)
                    && VillageIdentityReconciler.IsSameName(entry.VillageName, villageName))
                .ToList();
            return matches.Count == 1 ? matches[0].IsCapital : null;
        }
    }

    private void SaveCachedCapitalState(string villageName, bool? isCapital)
        => SaveCachedVillageState(villageName, isCapital, null, null);

    private void ClearCachedCapitalStatesForCurrentAccount()
    {
        lock (_capitalCacheSync)
        {
            EnsureCapitalCacheLoadedUnderLock();
            foreach (var key in _capitalCacheByKey.Keys.ToList())
            {
                var entry = _capitalCacheByKey[key];
                if (IsCurrentAccountServer(entry) && entry.IsCapital is not null)
                {
                    _capitalCacheByKey[key] = CopyCapitalCacheEntry(entry, isCapital: null);
                }
            }

            PersistCapitalCacheUnderLock();
        }
    }

    private void SaveCachedVillageState(string villageName, bool? isCapital, int? coordX, int? coordY)
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return;
        }

        lock (_capitalCacheSync)
        {
            EnsureCapitalCacheLoadedUnderLock();
            var key = BuildCapitalCacheKey(villageName, coordX, coordY);

            // Preserve existing coords if none provided; preserve existing isCapital if none provided
            _capitalCacheByKey.TryGetValue(key, out var existing);
            var resolvedIsCapital = isCapital ?? existing?.IsCapital;
            if (resolvedIsCapital is null && coordX is null && coordY is null)
            {
                return;
            }

            if (resolvedIsCapital == true)
            {
                foreach (var existingKey in _capitalCacheByKey.Keys.ToList())
                {
                    var other = _capitalCacheByKey[existingKey];
                    if (existingKey == key || !IsCurrentAccountServer(other) || other.IsCapital != true)
                    {
                        continue;
                    }

                    _capitalCacheByKey[existingKey] = CopyCapitalCacheEntry(other, isCapital: false);
                }
            }

            _capitalCacheByKey[key] = new CapitalCacheEntry
            {
                AccountName = _account.Name,
                ServerUrl = ServerUrl,
                VillageName = villageName,
                IsCapital = resolvedIsCapital,
                CoordX = coordX ?? existing?.CoordX,
                CoordY = coordY ?? existing?.CoordY,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            PersistCapitalCacheUnderLock();
        }
    }

    private (int? X, int? Y) TryGetCachedVillageCoords(string villageName)
    {
        if (string.IsNullOrWhiteSpace(villageName))
            return (null, null);
        EnsureCapitalCacheLoaded();
        lock (_capitalCacheSync)
        {
            var matches = _capitalCacheByKey.Values
                .Where(entry => IsCurrentAccountServer(entry)
                    && VillageIdentityReconciler.IsSameName(entry.VillageName, villageName))
                .ToList();
            return matches.Count == 1
                ? (matches[0].CoordX, matches[0].CoordY)
                : (null, null);
        }
    }

    private void EnsureCapitalCacheLoaded()
    {
        lock (_capitalCacheSync)
        {
            EnsureCapitalCacheLoadedUnderLock();
        }
    }

    private void EnsureCapitalCacheLoadedUnderLock()
    {
        if (_capitalCacheLoaded)
        {
            return;
        }

        _capitalCacheLoaded = true;
        _capitalCacheByKey.Clear();
        if (!File.Exists(_capitalCachePath))
        {
            MigrateLegacyCapitalCacheUnderLock();
            return;
        }

        try
        {
            var raw = File.ReadAllText(_capitalCachePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<CapitalCacheDocument>(raw, CapitalCacheJsonOptions);
            if (document?.Entries is null)
            {
                return;
            }

            foreach (var entry in document.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.VillageName))
                {
                    continue;
                }

                var key = BuildCapitalCacheEntryKey(entry);
                _capitalCacheByKey[key] = entry;
            }

            NormalizeConflictingCapitalCacheCandidatesUnderLock();
        }
        catch (Exception ex)
        {
            Notify($"Could not load capital cache: {ex.Message}");
        }
    }

    private void MigrateLegacyCapitalCacheUnderLock()
    {
        var legacyPath = AccountStoragePaths.LegacyCapitalStatePath(_projectRoot);
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var document = LoadCapitalCacheDocument(legacyPath);
            if (document?.Entries is null)
            {
                return;
            }

            var migrated = document.Entries
                .Where(entry => IsCapitalCacheEntryForAccount(entry, _account.Name))
                .ToList();
            if (migrated.Count == 0)
            {
                return;
            }

            foreach (var entry in migrated)
            {
                if (string.IsNullOrWhiteSpace(entry.VillageName))
                {
                    continue;
                }

                var key = BuildCapitalCacheEntryKey(entry);
                _capitalCacheByKey[key] = entry;
            }

            NormalizeConflictingCapitalCacheCandidatesUnderLock();
            PersistCapitalCacheUnderLock();
            RemoveMigratedAccountEntriesFromLegacyCapitalCache(legacyPath);
        }
        catch (Exception ex)
        {
            Notify($"Could not migrate legacy capital cache: {ex.Message}");
        }
    }

    private static CapitalCacheDocument? LoadCapitalCacheDocument(string path)
    {
        var raw = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : JsonSerializer.Deserialize<CapitalCacheDocument>(raw, CapitalCacheJsonOptions);
    }

    private void NormalizeConflictingCapitalCacheCandidatesUnderLock()
    {
        var candidates = _capitalCacheByKey
            .Where(pair => IsCurrentAccountServer(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.IsCapital, StringComparer.OrdinalIgnoreCase);
        var normalized = CapitalStateResolver.NormalizeCachedCapitalCandidates(candidates);
        if (normalized.Count == 0 || candidates.All(candidate => normalized[candidate.Key] == candidate.Value))
        {
            return;
        }

        foreach (var candidate in normalized)
        {
            var entry = _capitalCacheByKey[candidate.Key];
            _capitalCacheByKey[candidate.Key] = CopyCapitalCacheEntry(entry, candidate.Value);
        }

        PersistCapitalCacheUnderLock();
    }

    private void PersistCapitalCacheUnderLock()
    {
        try
        {
            var directory = Path.GetDirectoryName(_capitalCachePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Capital cache path is invalid.");
            }

            Directory.CreateDirectory(directory);
            var document = new CapitalCacheDocument
            {
                Entries = _capitalCacheByKey.Values
                    .OrderBy(item => item.AccountName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ServerUrl, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.VillageName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            File.WriteAllText(_capitalCachePath, JsonSerializer.Serialize(document, CapitalCacheJsonOptions));
            RemoveMigratedAccountEntriesFromLegacyCapitalCache(AccountStoragePaths.LegacyCapitalStatePath(_projectRoot));
        }
        catch (Exception ex)
        {
            Notify($"Could not save capital cache: {ex.Message}");
        }
    }

    private void RemoveMigratedAccountEntriesFromLegacyCapitalCache(string legacyPath)
    {
        if (!File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            var document = LoadCapitalCacheDocument(legacyPath);
            if (document?.Entries is null)
            {
                return;
            }

            var remaining = document.Entries
                .Where(entry => !IsCapitalCacheEntryForAccount(entry, _account.Name))
                .ToList();
            if (remaining.Count == document.Entries.Count)
            {
                return;
            }

            if (remaining.Count == 0)
            {
                File.Delete(legacyPath);
                return;
            }

            document = new CapitalCacheDocument { Entries = remaining };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(document, CapitalCacheJsonOptions));
        }
        catch (Exception ex)
        {
            Notify($"Could not prune legacy capital cache: {ex.Message}");
        }
    }

    private static bool IsCapitalCacheEntryForAccount(CapitalCacheEntry entry, string accountName)
    {
        if (string.IsNullOrWhiteSpace(entry.AccountName))
        {
            return false;
        }

        return string.Equals(
            AccountStoragePaths.NormalizeAccountKey(entry.AccountName),
            AccountStoragePaths.NormalizeAccountKey(accountName),
            StringComparison.Ordinal);
    }

    private bool IsCurrentAccountServer(CapitalCacheEntry entry)
        => IsCapitalCacheEntryForAccount(entry, _account.Name)
            && string.Equals(
                entry.ServerUrl.TrimEnd('/'),
                ServerUrl,
                StringComparison.OrdinalIgnoreCase);

    private string BuildCapitalCacheKey(string villageName, int? coordX = null, int? coordY = null)
    {
        var identity = coordX.HasValue && coordY.HasValue
            ? $"xy:{coordX.Value}|{coordY.Value}"
            : $"name:{villageName}";
        return CapitalCacheKey.Build(_account.Name, ServerUrl, identity);
    }

    private static string BuildCapitalCacheEntryKey(CapitalCacheEntry entry)
    {
        var identity = entry.CoordX.HasValue && entry.CoordY.HasValue
            ? $"xy:{entry.CoordX.Value}|{entry.CoordY.Value}"
            : $"name:{entry.VillageName}";
        return CapitalCacheKey.Build(entry.AccountName, entry.ServerUrl, identity);
    }

    private static CapitalCacheEntry CopyCapitalCacheEntry(CapitalCacheEntry entry, bool? isCapital) => new()
    {
        AccountName = entry.AccountName,
        ServerUrl = entry.ServerUrl,
        VillageName = entry.VillageName,
        IsCapital = isCapital,
        CoordX = entry.CoordX,
        CoordY = entry.CoordY,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class CapitalCacheDocument
    {
        [JsonPropertyName("entries")]
        public List<CapitalCacheEntry> Entries { get; init; } = [];
    }

    private sealed class CapitalCacheEntry
    {
        [JsonPropertyName("accountName")]
        public string AccountName { get; init; } = string.Empty;

        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; init; } = string.Empty;

        [JsonPropertyName("villageName")]
        public string VillageName { get; init; } = string.Empty;

        [JsonPropertyName("isCapital")]
        public bool? IsCapital { get; init; }

        [JsonPropertyName("coordX")]
        public int? CoordX { get; init; }

        [JsonPropertyName("coordY")]
        public int? CoordY { get; init; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }

}
