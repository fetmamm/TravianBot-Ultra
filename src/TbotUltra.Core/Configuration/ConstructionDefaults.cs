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

    public static int NormalizeStorageUpgradeLevelsAhead(int value) =>
        Math.Clamp(value, StorageUpgradeLevelsAheadMin, StorageUpgradeLevelsAheadMax);

    public static int NormalizeMainBuildingRebuildTargetLevel(int value) =>
        Math.Clamp(value, MainBuildingRebuildTargetLevelMin, MainBuildingRebuildTargetLevelMax);
}
