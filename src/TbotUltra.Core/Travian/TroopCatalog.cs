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

    private static string Normalize(string? value) =>
        string.Concat((value ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch))).Trim().ToLowerInvariant();
}
