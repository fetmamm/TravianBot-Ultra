namespace TbotUltra.Worker.Domain;

public sealed record MapOasisEntry(
    int X,
    int Y,
    bool IsOccupied,
    string OasisType,
    string FilterType,
    string Animals,
    string OwnerPlayer,
    string OwnerAlliance);

public sealed record MapOasisScanProgress(
    int CompletedAreas,
    int TotalAreas,
    int OasisCount);

