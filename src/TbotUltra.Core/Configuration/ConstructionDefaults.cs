namespace TbotUltra.Core.Configuration;

public static class ConstructionDefaults
{
    public const int StorageUpgradeLevelsAhead = 1;
    public const int StorageUpgradeLevelsAheadMin = 1;
    public const int StorageUpgradeLevelsAheadMax = 10;

    public static int NormalizeStorageUpgradeLevelsAhead(int value) =>
        Math.Clamp(value, StorageUpgradeLevelsAheadMin, StorageUpgradeLevelsAheadMax);
}
