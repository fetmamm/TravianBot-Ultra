namespace TbotUltra.Desktop.Services;

public sealed partial class VillageSettingsStore
{
    public RomanConstructionPriority GetRomanConstructionPriority(VillageKeyInfo village)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return RomanConstructionPriority.Auto;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return ParseRomanConstructionPriority(FindRecordByVillage(village)?.RomanConstructionPriority);
        }
    }

    public RomanConstructionPriority GetRomanConstructionPriority(string? villageKey)
    {
        if (string.IsNullOrWhiteSpace(villageKey))
        {
            return RomanConstructionPriority.Auto;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            return _cache.TryGetValue(NormalizeKey(villageKey), out var record)
                ? ParseRomanConstructionPriority(record.RomanConstructionPriority)
                : RomanConstructionPriority.Auto;
        }
    }

    public void SetRomanConstructionPriority(VillageKeyInfo village, RomanConstructionPriority priority)
    {
        if (village is null || string.IsNullOrWhiteSpace(village.Key))
        {
            return;
        }

        lock (FileIoLock)
        {
            EnsureCacheLoaded();
            var record = FindRecordByVillage(village);
            if (record is null)
            {
                var key = CanonicalKey(village);
                record = new VillageSettingRecord
                {
                    Key = key,
                    Name = village.Name,
                    CoordX = village.CoordX,
                    CoordY = village.CoordY,
                    IsCapital = village.IsCapital,
                    IsEnabled = DefaultAutomationEnabled,
                    EnabledGroups = CreateDefaultEnabledGroups(),
                    NpcTrade = false,
                    ConstructFasterEnabled = true,
                    HeroResourcesEnabled = true,
                };
                _cache[key] = record;
            }

            var value = priority.ToString();
            if (string.Equals(record.RomanConstructionPriority, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            record.RomanConstructionPriority = value;
            record.Name = village.Name;
            record.LastSeenUtc = DateTimeOffset.UtcNow;
            Save();
            _log?.Invoke($"Village '{village.Name}' Roman construction priority set to {value}.");
        }
    }

    private static RomanConstructionPriority ParseRomanConstructionPriority(string? value)
    {
        return Enum.TryParse<RomanConstructionPriority>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : RomanConstructionPriority.Auto;
    }
}
