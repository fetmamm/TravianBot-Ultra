namespace TbotUltra.Desktop.Services;

public static class ConstructionSlotCapacity
{
    public static int Resolve(string? tribe)
        => string.Equals(tribe?.Trim(), "Romans", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
}
