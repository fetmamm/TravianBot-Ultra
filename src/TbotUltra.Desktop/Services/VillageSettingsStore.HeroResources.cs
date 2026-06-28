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
/// a renamed village keeps its enabled choice instead of reappearing as a new village. The only village
/// on a new account defaults to enabled; villages discovered after that default to disabled. Construction
/// is the only automation group enabled by default. Explicit choices survive refreshes and restarts.
/// </summary>
public sealed partial class VillageSettingsStore
{
    /// <summary>Returns the remembered hero home village name for the active account (null if none).</summary>
    public string? GetHeroHomeVillageName()
    {
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _heroHomeVillageName;
        }
    }

    /// <summary>Persists the last-read hero home village name. No-op (no write) when unchanged.</summary>
    public void SetHeroHomeVillageName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            if (string.Equals(_heroHomeVillageName, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _heroHomeVillageName = trimmed;
            Save();
        }
    }

    public bool IsHeroResourcesEnabledByKey(string? key, bool defaultIfUnknown)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultIfUnknown;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(key), out var existing)
                ? existing.HeroResourcesEnabled ?? true
                : defaultIfUnknown;
        }
    }

    public bool GetHeroResourcesEnabled(VillageKeyInfo village)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return true;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return FindRecordByVillage(village)?.HeroResourcesEnabled ?? true;
        }
    }

    public HeroResourceSettings GetHeroResourceSettings(VillageKeyInfo village, HeroResourceSettings defaults)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return defaults;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return ResolveHeroResourceSettings(FindRecordByVillage(village), defaults);
        }
    }

    public HeroResourceSettings GetHeroResourceSettings(string? key, string? name, HeroResourceSettings defaults)
    {
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(name))
        {
            return defaults;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            VillageSettingRecord? record = null;
            if (!string.IsNullOrWhiteSpace(key))
            {
                _cache.TryGetValue(NormalizeKey(key), out record);
            }

            if (record is null && !string.IsNullOrWhiteSpace(name))
            {
                record = FindRecordByVillage(new VillageKeyInfo($"name:{name.Trim()}", name.Trim(), null, null, false));
            }

            return ResolveHeroResourceSettings(record, defaults);
        }
    }

    public void SetHeroResourcesEnabled(VillageKeyInfo village, bool enabled)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var record = FindRecordByVillage(village);
            if (record is not null)
            {
                if ((record.HeroResourcesEnabled ?? true) == enabled)
                {
                    return;
                }

                record.HeroResourcesEnabled = enabled;
                record.Name = village.Name;
                record.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                _cache[CanonicalKey(village)] = new VillageSettingRecord
                {
                    Key = CanonicalKey(village),
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = false,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = false,
                    HeroResourcesEnabled = enabled,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    public void SetHeroResourceSettings(VillageKeyInfo village, HeroResourceSettings settings)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        settings = settings with
        {
            MaxUseEnabled = true,
            MaxUsePerResource = Math.Max(0, settings.MaxUsePerResource),
        };

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var record = FindRecordByVillage(village);
            if (record is not null)
            {
                if (record.HeroResourcesEnabled.HasValue
                    && record.HeroResourceUseConstruction.HasValue
                    && record.HeroResourceUseSmithy.HasValue
                    && record.HeroResourceUseBrewery.HasValue
                    && record.HeroResourceUseTownHall.HasValue
                    && record.HeroResourceMaxUseEnabled.HasValue
                    && record.HeroResourceMaxUsePerResource.HasValue
                    && record.HeroResourcesEnabled.Value == settings.IsEnabled
                    && record.HeroResourceUseConstruction.Value == settings.UseConstruction
                    && record.HeroResourceUseSmithy.Value == settings.UseSmithy
                    && record.HeroResourceUseBrewery.Value == settings.UseBrewery
                    && record.HeroResourceUseTownHall.Value == settings.UseTownHall
                    && record.HeroResourceMaxUseEnabled.Value == settings.MaxUseEnabled
                    && record.HeroResourceMaxUsePerResource.Value == settings.MaxUsePerResource)
                {
                    return;
                }

                record.HeroResourcesEnabled = settings.IsEnabled;
                record.HeroResourceUseConstruction = settings.UseConstruction;
                record.HeroResourceUseSmithy = settings.UseSmithy;
                record.HeroResourceUseBrewery = settings.UseBrewery;
                record.HeroResourceUseTownHall = settings.UseTownHall;
                record.HeroResourceMaxUseEnabled = settings.MaxUseEnabled;
                record.HeroResourceMaxUsePerResource = settings.MaxUsePerResource;
                record.Name = village.Name;
                record.LastSeenUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                _cache[CanonicalKey(village)] = new VillageSettingRecord
                {
                    Key = CanonicalKey(village),
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = false,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = false,
                    HeroResourcesEnabled = settings.IsEnabled,
                    HeroResourceUseConstruction = settings.UseConstruction,
                    HeroResourceUseSmithy = settings.UseSmithy,
                    HeroResourceUseBrewery = settings.UseBrewery,
                    HeroResourceUseTownHall = settings.UseTownHall,
                    HeroResourceMaxUseEnabled = settings.MaxUseEnabled,
                    HeroResourceMaxUsePerResource = settings.MaxUsePerResource,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            }

            Save();
        }
    }

    private static HeroResourceSettings ResolveHeroResourceSettings(
        VillageSettingRecord? record,
        HeroResourceSettings defaults)
    {
        return record is null
            ? defaults
            : new HeroResourceSettings(
                record.HeroResourcesEnabled ?? true,
                record.HeroResourceUseConstruction ?? defaults.UseConstruction,
                record.HeroResourceUseSmithy ?? defaults.UseSmithy,
                record.HeroResourceUseBrewery ?? defaults.UseBrewery,
                record.HeroResourceUseTownHall ?? defaults.UseTownHall,
                MaxUseEnabled: true,
                Math.Max(0, record.HeroResourceMaxUsePerResource ?? defaults.MaxUsePerResource));
    }

}