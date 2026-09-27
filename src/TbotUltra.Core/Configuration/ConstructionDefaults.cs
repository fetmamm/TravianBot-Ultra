namespace TbotUltra.Core.Configuration;

public static class ConstructionDefaults
{
    public const bool MainBuildingRebuildEnabled = true;
    public const int MainBuildingRebuildTargetLevel = 1;
    public const int MainBuildingRebuildTargetLevelMin = 1;
    public const int MainBuildingRebuildTargetLevelMax = 20;
    public const bool CropShortageRecoveryEnabled = true;
    public const int StorageUpgradeLevelsAhead = 2;
    public const int StorageUpgradeLevelsAheadMin = 1;
    public const int StorageUpgradeLevelsAheadMax = 10;
    public const RomanConstructionPriority RomanPriority = RomanConstructionPriority.Auto;

    public static int NormalizeStorageUpgradeLevelsAhead(int value) =>
        Math.Clamp(value, StorageUpgradeLevelsAheadMin, StorageUpgradeLevelsAheadMax);

    public static int NormalizeMainBuildingRebuildTargetLevel(int value) =>
        Math.Clamp(value, MainBuildingRebuildTargetLevelMin, MainBuildingRebuildTargetLevelMax);

    public static RomanConstructionPriority NormalizeRomanPriority(RomanConstructionPriority value) =>
        Enum.IsDefined(value) ? value : RomanPriority;

    public static RomanConstructionPriority ParseRomanPriority(string? value) =>
        Enum.TryParse<RomanConstructionPriority>(value, ignoreCase: true, out var parsed)
            ? NormalizeRomanPriority(parsed)
            : RomanPriority;
}
