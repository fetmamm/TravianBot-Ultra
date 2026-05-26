namespace TbotUltra.Core.Configuration;

public sealed record ReinforcementTroopRule
{
    public string AccountName { get; init; } = string.Empty;
    public string SourceVillageName { get; init; } = string.Empty;
    public string TroopType { get; init; } = string.Empty;
    public string AmountMode { get; init; } = "fixed";
    public int Amount { get; init; } = 1;
    public bool IsEnabled { get; init; } = true;

    public bool UsesAllAvailable => string.Equals(AmountMode, "all_available", StringComparison.OrdinalIgnoreCase);
    public bool UsesPercentAvailable => PercentAvailable is not null;
    public int? PercentAvailable => AmountMode?.Trim().ToLowerInvariant() switch
    {
        "percent_20" => 20,
        "percent_50" => 50,
        "percent_90" => 90,
        _ => null,
    };

    public int NormalizedAmount => Math.Max(1, Amount);

    public ReinforcementTroopRule Normalize()
    {
        var normalizedAmountMode = PercentAvailable is { } percent
            ? $"percent_{percent}"
            : UsesAllAvailable ? "all_available" : "fixed";

        return this with
        {
            AccountName = AccountName.Trim(),
            SourceVillageName = SourceVillageName.Trim(),
            TroopType = TroopType.Trim(),
            AmountMode = normalizedAmountMode,
            Amount = Math.Max(1, Amount),
        };
    }
}
