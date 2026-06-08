namespace TbotUltra.Worker.Domain;

public sealed record TravcoRow(
    double? Distance,
    string Account,
    string Village,
    long? Pop,
    string Coordinates);

public sealed record TravcoScrapeResult(
    int PageNumber,
    int TotalPages,
    IReadOnlyList<TravcoRow> Rows);

public sealed record TravcoSearchRequest(
    int X,
    int Y,
    int DaysInactive,
    string OrderBy);

public sealed record TravcoRawRow(
    IReadOnlyList<string> Cells,
    string? VillageHref);

public sealed record TravcoRawPage(
    int PageNumber,
    int TotalPages,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TravcoRawRow> Rows);
