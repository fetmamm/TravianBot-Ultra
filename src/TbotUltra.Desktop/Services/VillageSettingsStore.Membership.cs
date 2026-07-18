using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Account-scoped persistence of which villages are enabled for automation, plus a small cache of
/// each village's identity (name/coords/capital). Stored per account in
/// <c>config/accounts/&lt;account&gt;/villages.json</c>.
///
/// Villages are keyed by their stable village key (the same <c>newdid</c>-based key the UI uses), so
/// a renamed village keeps its enabled choice instead of reappearing as a new village. New villages default
/// to enabled, while a user's explicit disabled choice is preserved. Construction
/// is the only automation group enabled by default. Explicit choices survive refreshes and restarts.
/// </summary>
public sealed partial class VillageSettingsStore
{
    /// <summary>
    /// Merges a freshly read set of villages into the store: new villages are enabled, and known villages
    /// keep their enabled choice while their cached identity (name/coords/capital) is refreshed. Only
    /// persists when something actually changed.
    /// Never removes villages absent from <paramref name="villages"/> (a page read can be partial).
    /// </summary>
    public void Merge(IReadOnlyList<VillageKeyInfo> villages)
    {
        if (villages is null || villages.Count == 0)
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();

            var added = 0;
            var updated = 0;
            var dirty = false;
            var validVillages = villages
                .Where(village => village is not null && !string.IsNullOrWhiteSpace(village.Key))
                .GroupBy(CanonicalKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var village in validVillages)
            {
                var key = CanonicalKey(village);
                if (_cache.TryGetValue(key, out var existing))
                {
                    // Keep the user's enabled choice; refresh cached identity if it changed.
                    if (!string.Equals(existing.Name, village.Name, StringComparison.Ordinal)
                        || existing.CoordX != village.CoordX
                        || existing.CoordY != village.CoordY
                        || existing.IsCapital != village.IsCapital)
                    {
                        existing.Name = village.Name;
                        existing.CoordX = village.CoordX;
                        existing.CoordY = village.CoordY;
                        existing.IsCapital = village.IsCapital;
                        updated++;
                        dirty = true;
                    }

                    existing.LastSeenUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    _cache[key] = new VillageSettingRecord
                    {
                        Key = key,
                        Name = village.Name,
                        CoordX = village.CoordX,
                        CoordY = village.CoordY,
                        IsCapital = village.IsCapital,
                        IsEnabled = DefaultAutomationEnabled,
                        EnabledGroups = CreateDefaultEnabledGroups(),
                        NpcTrade = false,
                        ConstructFasterEnabled = false,
                        HeroResourcesEnabled = true,
                        LastSeenUtc = DateTimeOffset.UtcNow,
                    };
                    added++;
                    dirty = true;
                }
            }

            if (dirty)
            {
                Save();
                _log?.Invoke(
                    $"Village settings merged: {added} new (Auto default={DefaultAutomationEnabled}, Construction only), {updated} updated.");
            }
        }
    }

    /// <summary>
    /// Applies a confirmed full village list from login/account scan: known villages missing from that
    /// list are no longer safe automation targets, so disable them without deleting their settings.
    /// Ordinary partial reads must keep using Merge only.
    /// </summary>
    public IReadOnlyList<string> DisableVillagesMissingFromConfirmedList(IReadOnlyList<VillageKeyInfo> villages)
    {
        if (villages is null || villages.Count == 0)
        {
            return Array.Empty<string>();
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();

            var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var village in villages.Where(village => village is not null && !string.IsNullOrWhiteSpace(village.Key)))
            {
                liveKeys.Add(CanonicalKey(village));
                if (!string.IsNullOrWhiteSpace(village.Name))
                {
                    liveNames.Add(village.Name.Trim());
                }
            }

            if (liveKeys.Count == 0 && liveNames.Count == 0)
            {
                return Array.Empty<string>();
            }

            var disabled = new List<string>();
            var now = DateTimeOffset.UtcNow;
            var dirty = false;
            foreach (var record in _cache.Values)
            {
                var keyStillLive = !string.IsNullOrWhiteSpace(record.Key) && liveKeys.Contains(record.Key);
                var nameStillLive = !string.IsNullOrWhiteSpace(record.Name) && liveNames.Contains(record.Name.Trim());

                // Pruning signal uses COORDINATE identity only: a lost village that was refounded under the
                // same name must still count as missing (its key is absent from the confirmed list) even
                // though a same-named live village exists. Only coordinate-keyed records are matched here;
                // a coordless record can't be confirmed gone, so it is never flagged for pruning.
                var hasCoords = record.CoordX.HasValue && record.CoordY.HasValue;
                if (hasCoords && !keyStillLive)
                {
                    if (record.ConfirmedMissingSinceUtc is null)
                    {
                        record.ConfirmedMissingSinceUtc = now;
                        dirty = true;
                    }
                }
                else if (record.ConfirmedMissingSinceUtc is not null)
                {
                    record.ConfirmedMissingSinceUtc = null;
                    dirty = true;
                }

                // Disabling stays conservative (key OR name) so a still-existing village seen under a
                // different key form is never disabled by mistake.
                if (record.IsEnabled && !keyStillLive && !nameStillLive)
                {
                    record.IsEnabled = false;
                    disabled.Add(string.IsNullOrWhiteSpace(record.Name) ? record.Key : record.Name);
                    dirty = true;
                }
            }

            if (dirty)
            {
                Save();
            }

            return disabled;
        }
    }

    /// <summary>
    /// Returns the villages that have been confirmed missing from the live list since at or before
    /// <paramref name="cutoff"/> (i.e. gone long enough to be treated as lost/destroyed). Read-only peek —
    /// does not remove anything. Callers use this to clean up the village's lingering queue items BEFORE
    /// <see cref="RemoveVillages"/> drops the records (so name-based key resolution still maps correctly).
    /// </summary>
    public IReadOnlyList<(string Key, string Name)> GetVillagesConfirmedMissingSince(DateTimeOffset cutoff)
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.Values
                .Where(record => record.ConfirmedMissingSinceUtc is DateTimeOffset since && since <= cutoff)
                .Select(record => (record.Key, record.Name ?? string.Empty))
                .ToList();
        }
    }

    /// <summary>
    /// Permanently removes the given village records by key and persists. Used by the lost-village cleanup
    /// after their queue items have been removed. No-op for keys that are not present.
    /// </summary>
    public int RemoveVillages(IReadOnlyCollection<string> keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return 0;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var removed = 0;
            foreach (var key in keys)
            {
                if (!string.IsNullOrWhiteSpace(key) && _cache.Remove(NormalizeKey(key)))
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                Save();
            }

            return removed;
        }
    }

}
