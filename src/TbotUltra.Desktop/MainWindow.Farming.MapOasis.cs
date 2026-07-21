using TbotUltra.Desktop.Models;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Runs an oasis scan and returns every recognized oasis (free and occupied, all bonus types).
    // Type/occupied filtering is applied later when adding farms, so the scan stores everything.
    // Automation is already paused by the Travco tools window that hosts this scan, so no extra
    // pause/resume handling is needed here.
    private async Task<List<OasisInfo>> RunMapOasisScanAsync(
        MapOasisScanRequest request,
        IProgress<MapOasisScanProgress> progress,
        CancellationToken cancellationToken)
    {
        return await RunManualOperationAsync(
            "Analyze Map Oasis",
            async token =>
            {
                var options = LoadBotOptions();
                var scanResult = await _botService.ScanMapOasesAsync(
                    options,
                    includeOccupied: true,
                    MapOasisAllTypes,
                    request,
                    AppendLog,
                    progress,
                    token);
                return scanResult.Oases.Select(entry => new OasisInfo
                {
                    X = entry.X,
                    Y = entry.Y,
                    Landscape = 0,
                    IsOccupied = entry.IsOccupied,
                    OasisType = entry.OasisType,
                    Animals = entry.Animals,
                    OwnerPlayer = entry.OwnerPlayer,
                    OwnerAlliance = entry.OwnerAlliance,
                }).ToList();
            },
            cancellationToken);
    }

    // Every oasis bonus type the parser recognizes; passing all of them makes the scan return every
    // oasis on the map.
    private static readonly IReadOnlyList<string> MapOasisAllTypes =
        Services.OasisListNaming.TypeOrder;
}
