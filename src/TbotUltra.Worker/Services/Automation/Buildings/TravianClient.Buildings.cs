using Microsoft.Playwright;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IBuildingClient
{

    public async Task<VillageStatus> ReadBuildingsStatusAsync(CancellationToken cancellationToken = default)
    {
        Notify("[build:verbose] ReadBuildingsStatusAsync started");
        var buildings = await ReadBuildingsAsync(cancellationToken);
        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var tribe = await ReadActiveVillageTribeAsync(cancellationToken);

        return new VillageStatus(
            ActiveVillage: activeVillage,
            Villages: [],
            Resources: new Dictionary<string, string>(),
            ResourceFields: [],
            Buildings: buildings,
            BuildQueue: [],
            Tribe: tribe,
            VillageCount: 0,
            IsCapital: TryGetCachedCapitalState(activeVillage),
            ServerTimeUtc: _serverTimeUtc);
    }

    internal static IReadOnlyList<Building> ParseBuildingOverviewHtmlForTests(string html)
    {
        var slots = BuildingDomParser.ExtractBuildingSlotHtml(html)
            .Select((slotHtml, index) =>
            {
                var className = BuildingDomParser.ReadAttribute(slotHtml, "class") ?? string.Empty;
                var labelText = BuildingDomParser.CleanHtmlText(Regex.Match(slotHtml, @"<div\b[^>]*class=[""'][^""']*\blabelLayer\b[^""']*[""'][^>]*>(?<text>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["text"].Value);
                var link = Regex.Match(slotHtml, @"<a\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["attrs"].Value;
                var dataName = BuildingDomParser.ReadAttribute(slotHtml, "data-name") ?? string.Empty;
                var dataLevel = BuildingDomParser.ReadAttribute(link, "data-level") ?? BuildingDomParser.ReadAttribute(slotHtml, "data-level") ?? string.Empty;
                return new BuildingOverviewSlotSnapshot
                {
                    Index = index,
                    ClassName = className,
                    OuterHtml = slotHtml,
                    LevelText = labelText,
                    DataLevelText = dataLevel,
                    DataNameText = dataName,
                    Text = BuildingDomParser.CleanHtmlText(slotHtml),
                    OccupiedEvidence = !string.IsNullOrWhiteSpace(link)
                        || !string.IsNullOrWhiteSpace(dataName)
                        || Regex.IsMatch(className, @"\bg\d{1,2}\b", RegexOptions.IgnoreCase),
                };
            })
            .ToList();

        return ParseBuildingOverviewScan(slots)
            .Buildings
            .Values
            .OrderBy(item => item.SlotId)
            .Select(item => new Building(
                item.SlotId,
                item.BuildingName,
                item.LevelKnown || !item.HasOccupancyEvidence ? item.Level : null,
                null,
                ParseGidFromBuildingCode(item.BuildingCode)))
            .ToList();
    }

    private async Task<bool> WaitForBuildSlotContextAsync(int slotId, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _page.WaitForFunctionAsync(
                """
                ({ slotId }) => {
                  // Must be the build page itself — dorf2.php?id=slot carries the same id= and even has
                  // build.php links, so the id+selector check alone would falsely pass on the overview.
                  if (!/build\.php/i.test(window.location.pathname)) return false;
                  const currentSlot = (() => {
                    const match = window.location.href.match(/[?&]id=(\d+)/);
                    return match ? Number(match[1]) : null;
                  })();
                  if (currentSlot !== slotId) return false;
                  return !!document.querySelector(
                    '#build, #contract, .upgradeBuilding, .contractWrapper, .buildingWrapper, .build_details, a[href*="build.php?id="]'
                  );
                }
                """,
                new { slotId },
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions BuildingOverviewSnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CurrentBuildPageSlotSnapshot
    {
        [JsonPropertyName("slotId")]
        public int? SlotId { get; set; }

        [JsonPropertyName("level")]
        public int? Level { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("gid")]
        public int? Gid { get; set; }

        [JsonPropertyName("hasBuildContext")]
        public bool HasBuildContext { get; set; }
    }

}

