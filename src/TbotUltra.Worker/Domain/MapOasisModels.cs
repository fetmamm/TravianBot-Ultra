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
    int OasisCount,
    bool IsPartialResult = false);

public sealed record MapOasisScanResult(
    IReadOnlyList<MapOasisEntry> Oases,
    int CompletedAreas,
    int TotalAreas,
    bool IsPartialResult = false);

public enum MapOasisScanScope
{
    WholeMap,
    Radius,
}

public enum MapOasisScanSpeed
{
    Normal,
    Fast,
}

public sealed record MapOasisScanRequest(
    int CenterX,
    int CenterY,
    MapOasisScanScope Scope,
    int Radius,
    MapOasisScanSpeed Speed)
{
    public const int DefaultRadius = 30;
}

public sealed record MapOasisScanInput(
    MapOasisScanRequest Request,
    bool IncludeOccupied,
    IReadOnlyCollection<string> SelectedTypes);

