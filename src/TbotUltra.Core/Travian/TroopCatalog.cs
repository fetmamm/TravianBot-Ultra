namespace TbotUltra.Core.Travian;

public static class TroopCatalog
{
    private static readonly IReadOnlyList<string> RomanTroops = ["Legionnaire", "Praetorian", "Imperian", "Equites Legati", "Equites Imperatoris", "Equites Caesaris", "Ram", "Fire Catapult", "Senator", "Settler"];
    private static readonly IReadOnlyList<string> GaulTroops = ["Phalanx", "Swordsman", "Pathfinder", "Theutates Thunder", "Druidrider", "Haeduan", "Ram", "Trebuchet", "Chieftain", "Settler"];
    private static readonly IReadOnlyList<string> TeutonTroops = ["Clubswinger", "Spearman", "Axeman", "Scout", "Paladin", "Teutonic Knight", "Ram", "Catapult", "Chief", "Settler"];
    private static readonly IReadOnlyList<string> HunTroops = ["Mercenary", "Bowman", "Spotter", "Steppe Rider", "Marksman", "Marauder", "Ram", "Catapult", "Logades", "Settler"];
    private static readonly IReadOnlyList<string> EgyptianTroops = ["Slave Militia", "Ash Warden", "Khopesh Warrior", "Sopdu Explorer", "Anhur Guard", "Resheph Chariot", "Ram", "Stone Catapult", "Nomarch", "Settler"];
    private static readonly IReadOnlyList<string> SpartanTroops = ["Hoplite", "Sentinel", "Shieldsman", "Twinsteel Therion", "Elpida Rider", "Corinthian Crusher", "Ram", "Ballista", "Ephor", "Settler"];
    private static readonly IReadOnlyList<string> FallbackTroops = ["Infantry 1", "Infantry 2", "Scout", "Cavalry 1", "Cavalry 2", "Ram", "Catapult"];

    private static readonly IReadOnlyList<IReadOnlyList<string>> AllTribeTroops =
    [
        RomanTroops,
        GaulTroops,
        TeutonTroops,
        HunTroops,
        EgyptianTroops,
        SpartanTroops,
        FallbackTroops,
    ];

    public static IReadOnlyList<string> ResolveTroopTypesForTribe(string? tribe)
    {
        var value = (tribe ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("roman"))
        {
            return RomanTroops;
        }

        if (value.Contains("gaul"))
        {
            return GaulTroops;
        }

        if (value.Contains("teuton"))
        {
            return TeutonTroops;
        }

        if (value.Contains("hun"))
        {
            return HunTroops;
        }

        if (value.Contains("egypt"))
        {
            return EgyptianTroops;
        }

        if (value.Contains("spartan"))
        {
            return SpartanTroops;
        }

        return FallbackTroops;
    }

    public static IReadOnlyList<string> ResolveTroopTypesForTribe(string? tribe, TroopTrainingBuildingType buildingType)
    {
        var allTroops = ResolveTroopTypesForTribe(tribe);
        var value = (tribe ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("teuton"))
        {
            return buildingType switch
            {
                TroopTrainingBuildingType.Barracks => allTroops.Take(4).ToList(),
                TroopTrainingBuildingType.Stable => allTroops.Skip(4).Take(2).ToList(),
                TroopTrainingBuildingType.Workshop => allTroops.Skip(6).Take(2).ToList(),
                _ => [],
            };
        }

        return buildingType switch
        {
            TroopTrainingBuildingType.Barracks => allTroops.Take(3).ToList(),
            TroopTrainingBuildingType.Stable => allTroops.Skip(3).Take(3).ToList(),
            TroopTrainingBuildingType.Workshop => allTroops.Skip(6).Take(2).ToList(),
            _ => [],
        };
    }

    public static bool IsTroopTypeAllowedForBuilding(string? tribe, string? troopType, TroopTrainingBuildingType buildingType)
    {
        var normalized = Normalize(troopType);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return ResolveTroopTypesForTribe(tribe, buildingType)
            .Any(item => string.Equals(Normalize(item), normalized, StringComparison.Ordinal));
    }

    public static int? ResolveTroopIndex(string? troopType)
    {
        var normalized = Normalize(troopType);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var troopSet in AllTribeTroops)
        {
            for (var index = 0; index < troopSet.Count; index++)
            {
                if (string.Equals(Normalize(troopSet[index]), normalized, StringComparison.Ordinal))
                {
                    return index + 1;
                }
            }
        }

        return null;
    }

    public static int? ResolveTravianUnitId(string? tribe, string? troopType)
    {
        var normalizedTroop = Normalize(troopType);
        if (string.IsNullOrWhiteSpace(normalizedTroop))
        {
            return null;
        }

        var baseId = ResolveTribeUnitBaseId(tribe);
        if (baseId is null)
        {
            return null;
        }

        var troopSet = ResolveTroopTypesForTribe(tribe);
        for (var index = 0; index < troopSet.Count; index++)
        {
            if (string.Equals(Normalize(troopSet[index]), normalizedTroop, StringComparison.Ordinal))
            {
                return baseId.Value + index;
            }
        }

        return null;
    }

    private static int? ResolveTribeUnitBaseId(string? tribe)
    {
        var value = (tribe ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("roman"))
        {
            return 1;
        }

        if (value.Contains("teuton"))
        {
            return 11;
        }

        if (value.Contains("gaul"))
        {
            return 21;
        }

        if (value.Contains("egypt"))
        {
            return 51;
        }

        if (value.Contains("hun"))
        {
            return 61;
        }

        if (value.Contains("spartan"))
        {
            return 71;
        }

        return null;
    }

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch))).Trim().ToLowerInvariant();
}
