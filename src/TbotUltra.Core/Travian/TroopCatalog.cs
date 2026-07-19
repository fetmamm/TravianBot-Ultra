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

    /// <summary>
    /// True when the value maps to a specific tribe's troop list. Unknown/empty values fall back
    /// to the generic list in <see cref="ResolveTroopTypesForTribe(string?)"/> — callers that would
    /// overwrite real troop data should check this first instead of trusting the fallback.
    /// </summary>
    public static bool IsKnownTribe(string? tribe)
    {
        var value = (tribe ?? string.Empty).Trim().ToLowerInvariant();
        return value.Contains("roman")
            || value.Contains("gaul")
            || value.Contains("teuton")
            || value.Contains("hun")
            || value.Contains("egypt")
            || value.Contains("spartan");
    }

    /// <summary>
    /// Maps Travian's numeric tribe id (used in DOM classes like "tribe8_medium" and on the profile
    /// villages table) to the tribe name this codebase uses elsewhere. Returns null for ids we do not
    /// play — 4 (Nature) and 5 (Natars) are NPC tribes and never belong to a player village.
    /// </summary>
    public static string? ResolveTribeFromTravianId(int tribeId)
    {
        return tribeId switch
        {
            1 => "Romans",
            2 => "Teutons",
            3 => "Gauls",
            6 => "Egyptians",
            7 => "Huns",
            8 => "Spartans",
            _ => null,
        };
    }

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
        if (!IsKnownTribe(tribe))
        {
            // The 7-item fallback list has its own layout (no chief/settler tail), so the generic
            // 3/3/2 split below would put Ram in the Stable and leave the Workshop with only Catapult.
            return buildingType switch
            {
                TroopTrainingBuildingType.Barracks => allTroops.Take(3).ToList(),
                TroopTrainingBuildingType.Stable => allTroops.Skip(3).Take(2).ToList(),
                TroopTrainingBuildingType.Workshop => allTroops.Skip(5).Take(2).ToList(),
                _ => [],
            };
        }

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

        if (value.Contains("gaul"))
        {
            return buildingType switch
            {
                TroopTrainingBuildingType.Barracks => allTroops.Take(2).ToList(),
                TroopTrainingBuildingType.Stable => allTroops.Skip(2).Take(4).ToList(),
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
