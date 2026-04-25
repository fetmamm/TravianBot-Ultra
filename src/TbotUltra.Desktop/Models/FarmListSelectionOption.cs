namespace TbotUltra.Desktop.Models;

public sealed class FarmListSelectionOption
{
    public string Name { get; init; } = string.Empty;
    public int ActiveFarmCount { get; init; }
    public int TotalFarmCount { get; init; }

    public int AvailableSlots => Math.Max(0, TotalFarmCount - ActiveFarmCount);

    public string CountText => $"{ActiveFarmCount}/{TotalFarmCount}";

    public string CapacityText => $"{AvailableSlots} slots left";
}
