using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building overview navigation, DOM snapshots and parsing entrypoints.
public sealed partial class TravianClient
{
    private async Task<IReadOnlyList<Building>> ReadBuildingsAsync(CancellationToken cancellationToken)
    {
        using var trace = _browserTrace.BeginOperation("READ", "buildings-overview", "scope=dorf2");
        await GotoAsync(Paths.Buildings, cancellationToken);

        await EnsureLoggedInAsync();

        Dictionary<int, BuildingInfo> buildingsBySlot = new();
        await RetryAsync("read building slots snapshot", async () =>
        {
            buildingsBySlot = await ReadBuildingInfosAsync(cancellationToken);
        }, cancellationToken: cancellationToken);

        var result = buildingsBySlot.Values
            .OrderBy(item => item.SlotId)
            .Select(item => new Building(
                item.SlotId,
                item.BuildingName,
                item.LevelKnown || !item.HasOccupancyEvidence ? item.Level : null,
                ResolveUrl(Paths.BuildBySlot(item.SlotId)),
                ParseGidFromBuildingCode(item.BuildingCode)))
            .ToList();
        trace.Complete("success", $"source=live count={result.Count}");
        return result;
    }

    private async Task<Dictionary<int, BuildingInfo>> ReadBuildingInfosAsync(CancellationToken cancellationToken)
    {
        var firstScan = await ScanBuildingOverviewAsync(cancellationToken);
        if (!BuildingOverviewScanPolicy.ShouldRetry(firstScan.Metrics))
        {
            return firstScan.Buildings;
        }

        Notify($"Building overview scan looked incomplete ({BuildingOverviewScanPolicy.Describe(firstScan.Metrics)}). Reloading once.");

        await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);

        await EnsureLoggedInAsync();

        var secondScan = await ScanBuildingOverviewAsync(cancellationToken);
        return BuildingOverviewScanPolicy.PreferSecond(firstScan.Metrics, secondScan.Metrics)
            ? secondScan.Buildings
            : firstScan.Buildings;
    }

    private async Task<BuildingOverviewScanResult> ScanBuildingOverviewAsync(CancellationToken cancellationToken)
    {
        await WaitForBuildingOverviewReadyAsync(cancellationToken);
        var slots = await ReadBuildingOverviewSlotSnapshotsAsync(cancellationToken);
        return ParseBuildingOverviewScan(slots);
    }

    private async Task WaitForBuildingOverviewReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _page.WaitForFunctionAsync(
                """
                () => {
                  const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
                  if (slots.length < 18) {
                    return false;
                  }

                  // Wait until building images have populated their gid classes.
                  // V3 layouts add `g<gid>` to the slot/img after async hydration; if we read
                  // too early, occupied slots look empty (missing_gid spam in logs).
                  const slotsWithGid = slots.filter(slot => {
                    const slotClass = String(slot.className || '');
                    if (/\bg\d{1,2}\b/i.test(slotClass)) return true;
                    const img = slot.querySelector('img.building, img[class*=" g"], img[class^="g"]');
                    return img && /\bg\d{1,2}\b/i.test(String(img.className || ''));
                  }).length;

                  // Typical T4 villages have 18+ slots with at least ~10 occupied buildings
                  // by the time the player builds anything; require a reasonable share to
                  // confirm the page has hydrated.
                  return slotsWithGid >= Math.min(slots.length, 12);
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 4000 });
        }
        catch (TimeoutException)
        {
            // Continue with the best available DOM snapshot.
        }
        catch (Exception ex) when (!IsTransientExecutionContextException(ex))
        {
            Notify($"Building overview ready wait skipped: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<BuildingOverviewSlotSnapshot>> ReadBuildingOverviewSlotSnapshotsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => String(value || '').replace(/\s+/g, ' ').trim();
              const slots = Array.from(document.querySelectorAll('div.buildingSlot'));
              return JSON.stringify(slots.map((slot, index) => {
                const label = slot.querySelector('.labelLayer, .level, .label');
                const namedElement = slot.querySelector('.name, .title, .desc, .buildingName, .hover, [title], [aria-label], img[alt]');
                const link = slot.querySelector('a[href], area[href]');
                const image = slot.querySelector('img[alt]');
                const dataName = clean(slot.getAttribute('data-name') || '');
                const dataLevel = clean(slot.getAttribute('data-level') || link?.getAttribute('data-level') || '');
                const buildingImg = slot.querySelector('img.building, img[class*=" g"], img[class^="g"]');
                const buildingImgClass = buildingImg ? String(buildingImg.className || '') : '';
                const text = clean(slot.textContent || '');
                const slotOwnClass = String(slot.className || '');
                // V3 layouts sometimes leave the gid class only on the inner <img>.
                // Merge both so the C# parser sees `g<gid>` regardless of which element carries it.
                const className = (slotOwnClass + ' ' + buildingImgClass).trim();
                const occupiedEvidence =
                  /\bg\d{1,2}\b/i.test(className)
                  || /underconst|underconstruction|built|occupied/i.test(className)
                  || Boolean(link)
                  || /\blevel\s*\d+\b/i.test(text);

                return {
                  index,
                  className,
                  outerHtml: slot.outerHTML || '',
                  levelText: clean(label ? label.textContent : ''),
                  dataLevelText: dataLevel,
                  dataNameText: dataName,
                  nameText: clean(namedElement ? namedElement.textContent : ''),
                  titleText: clean(slot.getAttribute('title') || (namedElement ? namedElement.getAttribute('title') : '') || (link ? link.getAttribute('title') : '') || ''),
                  altText: clean(image ? image.getAttribute('alt') : ''),
                  text,
                  occupiedEvidence
                };
              }));
            }
            """);

        return JsonSerializer.Deserialize<List<BuildingOverviewSlotSnapshot>>(
            rawJson ?? "[]",
            BuildingOverviewSnapshotJsonOptions) ?? [];
    }

    private static BuildingOverviewScanResult ParseBuildingOverviewScan(
        IReadOnlyList<BuildingOverviewSlotSnapshot> slotSnapshots) =>
        BuildingOverviewDomParser.Parse(slotSnapshots);

    private static int? ParseGidFromBuildingCode(string? buildingCode) =>
        BuildingOverviewDomParser.ParseGidFromBuildingCode(buildingCode);

    private static string? TryResolveBuildingCodeFromName(params string?[] candidates) =>
        BuildingOverviewDomParser.TryResolveBuildingCodeFromName(candidates);

    private static string ResolveBuildingDisplayName(
        string? buildingCode,
        string? nameCandidate,
        bool hasOccupancyEvidence) =>
        BuildingOverviewDomParser.ResolveBuildingDisplayName(
            buildingCode,
            nameCandidate,
            hasOccupancyEvidence);

    private static string? SelectBuildingNameCandidate(params string?[] candidates) =>
        BuildingOverviewDomParser.SelectBuildingNameCandidate(candidates);

}
