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

public sealed record MapOasisApiCapture(
    string Url,
    int CenterX,
    int CenterY,
    int ZoomLevel,
    string Json);

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
    public const int DefaultRadius = 40;
}

public sealed record MapOasisScanEstimate(
    int Zoom3Requests,
    int Zoom2Requests,
    int Zoom1Requests)
{
    public static MapOasisScanEstimate Calculate(MapOasisScanRequest request)
    {
        var minimumX = request.Scope == MapOasisScanScope.Radius ? Math.Max(-200, request.CenterX - request.Radius) : -200;
        var maximumX = request.Scope == MapOasisScanScope.Radius ? Math.Min(200, request.CenterX + request.Radius) : 200;
        var minimumY = request.Scope == MapOasisScanScope.Radius ? Math.Max(-200, request.CenterY - request.Radius) : -200;
        var maximumY = request.Scope == MapOasisScanScope.Radius ? Math.Min(200, request.CenterY + request.Radius) : 200;
        var width = Math.Max(0, maximumX - minimumX + 1);
        var height = Math.Max(0, maximumY - minimumY + 1);

        return new MapOasisScanEstimate(
            CountAreas(width, 31) * CountAreas(height, 31),
            CountAreas(width, 21) * CountAreas(height, 17),
            CountAreas(width, 11) * CountAreas(height, 9));
    }

    private static int CountAreas(int coordinateCount, int areaSize)
        => coordinateCount == 0 ? 0 : (coordinateCount + areaSize - 1) / areaSize;
}

public sealed record MapOasisScanInput(
    MapOasisScanRequest Request,
    bool IncludeOccupied,
    IReadOnlyCollection<string> SelectedTypes);

