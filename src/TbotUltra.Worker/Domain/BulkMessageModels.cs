namespace TbotUltra.Worker.Domain;

public enum BulkMessageSortOrder
{
    PopulationDescending = 0,
    PopulationAscending = 1,
}

public sealed record BulkMessagePlayer(
    string Name,
    string? Alliance,
    long Population,
    int VillageCount);

public sealed record BulkMessageAnalyzeRequest(
    IReadOnlyList<string> ExcludedPlayers,
    IReadOnlyList<string> ExcludedAlliances,
    BulkMessageSortOrder SortOrder);

public sealed record BulkMessageAnalyzeResult(
    int PlayersAnalyzed,
    int EligiblePlayers,
    int SentCachedCount,
    IReadOnlyList<BulkMessagePlayer> Players);

public sealed record BulkMessageRequest(
    string Subject,
    string Message,
    int MaxRecipients,
    IReadOnlyList<string> ExcludedPlayers,
    IReadOnlyList<string> ExcludedAlliances,
    BulkMessageSortOrder SortOrder);

public sealed record BulkMessageProgress(
    string Phase,
    int SentCount,
    int TargetCount,
    int BatchNumber = 0,
    int TotalBatches = 0,
    string? CurrentPlayers = null);

public sealed record BulkMessageSendResult(
    int PlayersAnalyzed,
    int TargetCount,
    int SentCount,
    int SentCachedCount);
