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

    private async Task<bool?> ReadIsCapitalAsync(string villageName, CancellationToken cancellationToken)
    {
        Notify("[ReadIsCapitalAsync] started for village.");
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading capital state.", cancellationToken);
        var previousUrl = _page.Url;
        try
        {
            await GotoAsync(Paths.PlayerProfile, cancellationToken);
            await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading player profile.", cancellationToken);
            var result = await _page.EvaluateAsync<string>(
                """
                (vName) => {
                  const clean = (v) => (v || '').replace(/\s+/g, ' ').trim();
                  const wanted = clean(vName).toLowerCase();
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
                  return rowText.includes(wanted) ? 'true' : 'false';
                }
                """,
                villageName);

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

        var isCapital = await ReadIsCapitalAsync(activeVillage, cancellationToken);
        SaveCachedCapitalState(activeVillage, isCapital);
    }

    private async Task RefreshCapitalStatesFromPlayerProfileAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading capital states.", cancellationToken);
        await EnsureLoggedInAsync();

        // ReadVillagesAsync visits spieler.php (or returns the session cache) and already calls
        // SaveCachedVillageState for each village. We just need to log the capital detection.
        var villages = await ReadVillagesAsync(cancellationToken);
        if (villages.Count == 0)
        {
            Notify("Capital detection: no village data found - falling back to active village check.");
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
            return;
        }

        var capitalName = villages.FirstOrDefault(v => v.IsCapital == true)?.Name ?? string.Empty;
        Notify($"Capital detection: identified capital village as '{(string.IsNullOrWhiteSpace(capitalName) ? "(none)" : capitalName)}'. Found {villages.Count} villages with coords.");
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
    {
        if (string.IsNullOrWhiteSpace(villageName))
        {
            return null;
        }

        EnsureCapitalCacheLoaded();
        var key = BuildCapitalCacheKey(villageName);
        lock (_capitalCacheSync)
        {
            return _capitalCacheByKey.TryGetValue(key, out var entry)
                ? entry.IsCapital
                : null;
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
            var key = BuildCapitalCacheKey(villageName);

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
                ServerUrl = _config.BaseUrl.TrimEnd('/'),
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
        var key = BuildCapitalCacheKey(villageName);
        lock (_capitalCacheSync)
        {
            return _capitalCacheByKey.TryGetValue(key, out var entry)
                ? (entry.CoordX, entry.CoordY)
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

                var key = CapitalCacheKey.Build(entry.AccountName, entry.ServerUrl, entry.VillageName);
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

                var key = CapitalCacheKey.Build(entry.AccountName, entry.ServerUrl, entry.VillageName);
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

    private string BuildCapitalCacheKey(string villageName)
    {
        return CapitalCacheKey.Build(_account.Name, _config.BaseUrl.TrimEnd('/'), villageName);
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
