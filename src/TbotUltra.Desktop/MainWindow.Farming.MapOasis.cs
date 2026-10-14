using System.IO;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private bool HasMapOasisScanResult()
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        var options = LoadBotOptions();
        return File.Exists(AccountStoragePaths.MapOasisCachePath(_projectRoot, accountName, options.BaseUrl));
    }

    // Runs an oasis scan and returns every recognized oasis (free and occupied, all bonus types).
    // Type/occupied filtering is applied later when adding farms, so the scan stores everything.
    // Automation is already paused by the Travco tools window that hosts this scan, so no extra
    // pause/resume handling is needed here.
    private Task<MapOasisScanResult> RunMapOasisScanAsync(
        MapOasisScanRequest request,
        IProgress<MapOasisScanProgress> progress,
        CancellationToken cancellationToken)
    {
        return RunManualOperationAsync(
            "Analyze Map Oasis",
            async token =>
            {
                var options = LoadBotOptions();
                return await _botService.ScanMapOasesAsync(
                    options,
                    includeOccupied: true,
                    MapOasisAllTypes,
                    request,
                    AppendLog,
                    progress,
                    token);
            },
            cancellationToken);
    }

    // Every oasis bonus type the parser recognizes; passing all of them makes the scan return every
    // oasis on the map.
    private static readonly IReadOnlyList<string> MapOasisAllTypes =
        Services.OasisListNaming.TypeOrder;
}
