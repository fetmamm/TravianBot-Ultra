using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Core.Accounts;

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
                    const parseCoordinate = (value) => {
                      const match = clean(value).replace(/[−–—]/g, '-').match(/-?\d+/);
                      return match ? Number.parseInt(match[0], 10) : null;
                    };
                    const rowX = parseCoordinate(row.querySelector('.coordinateX, .coordinate.x')?.textContent || '');
                    const rowY = parseCoordinate(row.querySelector('.coordinateY, .coordinate.y')?.textContent || '');
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
