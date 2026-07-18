using Microsoft.Playwright;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Configuration;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed record UiSyncVillage(
        string Name,
        string? Url,
        bool? IsCapital,
        int? CoordX,
        int? CoordY,
        int? Population,
        int? CropFields);
    private sealed record UiSyncSnapshot(
        int? Gold,
        int? Silver,
        string ActiveVillage,
        int? ActiveVillageCoordX,
        int? ActiveVillageCoordY,
        IReadOnlyList<UiSyncVillage> Villages);

    private async Task TryEmitUiSyncSnapshotAsync(CancellationToken cancellationToken, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && (now - _lastUiSyncAt) < UiSyncMinInterval)
        {
            return;
        }

        try
        {
            var currency = await ReadCurrencyAsync(cancellationToken);
            var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
            var activeCoordinates = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
            // If the active village was renamed in-game, refresh its cached name now (matched by
            // coordinates) so the villages list below agrees with ActiveVillage in the same payload.
            ReconcileActiveVillageNameInCache(activeVillage, activeCoordinates);
            var villages = await ReadVillagesPreferCacheAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new UiSyncSnapshot(
                Gold: currency.Gold,
                Silver: currency.Silver,
                ActiveVillage: activeVillage,
                ActiveVillageCoordX: activeCoordinates.X,
                ActiveVillageCoordY: activeCoordinates.Y,
                Villages: villages
                    .Select(v => new UiSyncVillage(v.Name, v.Url, v.IsCapital, v.CoordX, v.CoordY, v.Population, v.CropFields))
                    .ToList()));
            _lastUiSyncAt = now;
            Notify($"[ui-sync] {payload}");
        }
        catch (Exception ex)
        {
            Notify($"UI sync snapshot failed: {ex.Message}");
        }
    }

}
