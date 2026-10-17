using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed record ExpansionStepResult(bool Completed, int? WaitSeconds, string Message);

public sealed partial class TravianClient
{
    private const string SettlerTroopType = "Settler";
    private const int SettlerSlot = 10;

    private static readonly (ExpansionBuildingType Type, string Name)[] ExpansionBuildings =
    [
        (ExpansionBuildingType.Palace, "Palace"),
        (ExpansionBuildingType.Residence, "Residence"),
        (ExpansionBuildingType.CommandCenter, "Command Center"),
    ];

    /// <summary>
    /// Trains the settlers still needed for the free expansion slots of the active village's
    /// Residence/Palace/Command Center. Completes when the slots are covered.
    /// </summary>
    public async Task<ExpansionStepResult> TrainSettlersAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var status = await ReadBuildingsStatusAsync(cancellationToken);
        var expansion = FindExpansionBuilding(status.Buildings);
        if (expansion is null)
        {
            return new ExpansionStepResult(false, 3600, "Train settlers: no Residence, Palace or Command Center in this village yet.");
        }

        var (building, type) = expansion.Value;
        var level = building.Level ?? 0;
        var usedSlots = level >= 10
            ? await ReadUsedExpansionSlotsAsync(building.SlotId!.Value, status.Villages.Count, cancellationToken)
            : 0;
        var settlersAtHome = await ReadSettlersAtHomeAsync(cancellationToken);

        // The Residence/Palace opens on its management tab; settlers are trained on tab s=1.
        var trainingPath = $"{Paths.BuildBySlot(building.SlotId!.Value)}&s=1";
        await EnsurePageForReadAsync(trainingPath, "train settlers", cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var queue = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var settlersInTraining = CountSettlersInQueue(queue);

        // Used slots are approximated by the number of own villages beyond the first; exact per-village
        // expansion data lives on the Residence expansion tab and is not read yet.
        var plan = ExpansionCalculator.PlanSettlerTraining(type, level, usedSlots, settlersAtHome, settlersInTraining, 0);
        Notify($"[expansion] {type} level {level}: slots={plan.ExpansionSlots}, free={plan.FreeSlots}, home={settlersAtHome}, training={settlersInTraining}, toTrain={plan.SettlersToTrain}. {plan.Reason}");
        if (plan.ExpansionSlots == 0)
        {
            return new ExpansionStepResult(false, 3600, $"Train settlers: {plan.Reason}");
        }

        if (plan.FreeSlots == 0 && ExpansionCalculator.NextSlotLevel(type, level) is int nextSlotLevel)
        {
            Notify($"[expansion] all {plan.ExpansionSlots} slot(s) of {type} used; waiting for level {nextSlotLevel} to unlock the next one.");
            return new ExpansionStepResult(false, 3600, $"Train settlers: waiting for {type} level {nextSlotLevel} for the next expansion slot.");
        }

        if (plan.SettlersToTrain == 0)
        {
            return new ExpansionStepResult(true, null, $"Train settlers: done. {plan.Reason}");
        }

        var unitId = TroopCatalog.ResolveTravianUnitId(status.Tribe, SettlerTroopType);
        var inputName = await ResolveTroopTrainingInputNameAsync(unitId ?? 0, SettlerSlot);
        if (string.IsNullOrWhiteSpace(inputName))
        {
            Notify($"[expansion] settler input not found on '{_page.Url}'.");
            return new ExpansionStepResult(false, 1800, "Train settlers: settler training input not found on the building page.");
        }

        var buildInfo = await ReadTroopUnitBuildInfoFromCurrentPageAsync(inputName, cancellationToken);
        if (!buildInfo.Found || !buildInfo.CanTrain)
        {
            return new ExpansionStepResult(false, 1200, "Train settlers: settlers cannot be trained right now (resources or requirements).");
        }

        var liveSnapshot = await ReadTroopTrainingResourceSnapshotFromCurrentPageAsync(cancellationToken);
        var liveResources = liveSnapshot.Resources;
        var affordable = MaxAffordable(liveResources, buildInfo);
        Notify($"[expansion] live resources wood={liveResources.GetValueOrDefault("wood")}, clay={liveResources.GetValueOrDefault("clay")}, iron={liveResources.GetValueOrDefault("iron")}, crop={liveResources.GetValueOrDefault("crop")}; affordable settlers={affordable}.");
        var amount = Math.Min(plan.SettlersToTrain, affordable);
        if (amount <= 0)
        {
            var cost = new Dictionary<string, long>
            {
                ["wood"] = buildInfo.WoodCost,
                ["clay"] = buildInfo.ClayCost,
                ["iron"] = buildInfo.IronCost,
                ["crop"] = buildInfo.CropCost,
            };
            var secondsUntilAffordable = ExpansionCalculator.SecondsUntilAffordable(liveResources, liveSnapshot.ProductionByHour, cost);
            // A short buffer covers rounding of the displayed production; unknown production falls back to 20 minutes.
            var wait = secondsUntilAffordable is int seconds ? Math.Clamp(seconds + 30, 60, 3600) : 1200;
            Notify($"[expansion] resources for one settler in {secondsUntilAffordable?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}s; waiting {wait}s.");
            return new ExpansionStepResult(false, wait, $"Train settlers: not enough resources for one settler (needs {buildInfo.WoodCost}/{buildInfo.ClayCost}/{buildInfo.IronCost}/{buildInfo.CropCost}).");
        }

        await DelayBeforeClickAsync(cancellationToken, "train settlers");
        if (!await SubmitTroopTrainingFromCurrentPageAsync(inputName, amount, useMaxShortcut: false, cancellationToken))
        {
            return new ExpansionStepResult(false, 900, $"Train settlers: submitting {amount} settler(s) failed.");
        }

        // The submit helper can report success although Travian rejected the order, so confirm it in the queue.
        await GotoAsync(trainingPath, cancellationToken);
        var settlersInTrainingAfter = CountSettlersInQueue(await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken));
        if (settlersInTrainingAfter < settlersInTraining + amount)
        {
            Notify($"[expansion] settler order not confirmed: training before={settlersInTraining}, after={settlersInTrainingAfter}, ordered={amount}.");
            return new ExpansionStepResult(false, 900, $"Train settlers: order for {amount} settler(s) was not confirmed in the training queue.");
        }

        Notify($"[expansion] queued {amount} settler(s) at {type}.");
        var remaining = plan.SettlersToTrain - amount;
        var trainingSeconds = (buildInfo.TrainingSeconds ?? 600) * amount;
        return remaining > 0
            ? new ExpansionStepResult(false, Math.Clamp(trainingSeconds, 600, 3600), $"Train settlers: queued {amount}, {remaining} still needed.")
            : new ExpansionStepResult(true, null, $"Train settlers: queued {amount}; all free slots are covered.");
    }

    /// <summary>
    /// Sends 3 settlers from the active village to found a new village on the given free field. The tribe is
    /// only asked for on the account's first foundings; without one the account's own tribe is used.
    /// </summary>
    public async Task<ExpansionStepResult> FoundVillageAsync(int x, int y, int? tribeId, CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var settlersAtHome = await ReadSettlersAtHomeAsync(cancellationToken);
        if (settlersAtHome < ExpansionCalculator.SettlersPerVillage)
        {
            var wait = await EstimateSettlerTrainingWaitSecondsAsync(cancellationToken);
            return new ExpansionStepResult(false, wait, $"Found village ({x}|{y}): only {settlersAtHome}/{ExpansionCalculator.SettlersPerVillage} settlers at home.");
        }

        var cultureWait = await ReadCulturePointsWaitSecondsAsync(cancellationToken);
        if (cultureWait is int cultureSeconds)
        {
            return new ExpansionStepResult(false, cultureSeconds, $"Found village ({x}|{y}): not enough culture points for another village yet.");
        }

        var detailsPath = $"/position_details.php?x={x.ToString(CultureInfo.InvariantCulture)}&y={y.ToString(CultureInfo.InvariantCulture)}";
        await GotoAsync(detailsPath, cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);

        var foundLink = await FindFoundVillageLinkAsync();
        if (foundLink is null)
        {
            await LogPageLinksForDiagnosisAsync("found village link");
            return new ExpansionStepResult(false, null, $"Found village ({x}|{y}): no 'Found new village' action on the field page (occupied, oasis, or not enough culture points?).");
        }

        await DelayBeforeClickAsync(cancellationToken, "found new village");
        await ClickLocatorAsync(foundLink, "found-new-village", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
        Notify($"[expansion] after 'Found new village' url='{_page.Url}'.");

        if (await _page.Locator("#content select[name='vid']").CountAsync() > 0)
        {
            var chosenTribe = tribeId ?? await ResolveOwnTribeIdAsync(cancellationToken);
            if (chosenTribe is null)
            {
                return new ExpansionStepResult(false, null, $"Found village ({x}|{y}): the game asks for a tribe and the own tribe is unknown; set one in the founding task.");
            }

            // Choosing a tribe reloads the form with &tribe=N, so load that URL directly.
            Notify($"[expansion] tribe selection required; choosing tribe {chosenTribe}.");
            await GotoAsync($"{_page.Url}&tribe={chosenTribe.Value.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        }

        var submit = _page.Locator("#content button.green[type='submit']:not([disabled]), #content form button[type='submit']:not([disabled])").First;
        if (await submit.CountAsync() == 0)
        {
            var error = await ReadReinforcementFormErrorAsync(cancellationToken);
            await LogPageLinksForDiagnosisAsync("found village form");
            return new ExpansionStepResult(false, null, $"Found village ({x}|{y}): no enabled send button on the founding form{(error is null ? "." : $": {error}")}");
        }

        await DelayBeforeClickAsync(cancellationToken, "found village: send");
        await ClickLocatorAsync(submit, "found-village-send", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);

        // Older layouts show a confirmation page after the form.
        var confirm = _page.Locator("button#confirmSendTroops, button[name='confirmSendTroops'], button.rallyPointConfirm").First;
        if (await confirm.CountAsync() > 0)
        {
            await DelayBeforeClickAsync(cancellationToken, "found village: confirm");
            await ClickLocatorAsync(confirm, "found-village-confirm", cancellationToken);
            await WaitForSendTroopsCompletionAsync(cancellationToken);
        }

        var settlersAfter = await ReadSettlersAtHomeAsync(cancellationToken);
        if (settlersAfter > settlersAtHome - ExpansionCalculator.SettlersPerVillage)
        {
            var error = await ReadReinforcementFormErrorAsync(cancellationToken);
            await LogPageLinksForDiagnosisAsync("found village result");
            return new ExpansionStepResult(false, 900, $"Found village ({x}|{y}): settlers did not leave (home before={settlersAtHome}, after={settlersAfter}){(error is null ? "." : $": {error}")}");
        }

        Notify($"[expansion] settlers sent to found ({x}|{y}).");
        return new ExpansionStepResult(true, null, $"Found village ({x}|{y}): settlers sent.");
    }

    /// <summary>
    /// Seconds until the settlers in training are done, read from the Residence/Palace queue. Falls back to
    /// 30 minutes when no settler is in training or the building is missing.
    /// </summary>
    // Expansion slots belong to the village whose Residence/Palace founded or conquered the other village,
    // so the account-wide village count overstates used slots in every village but the founding one.
    private async Task<int> ReadUsedExpansionSlotsAsync(int buildingSlotId, int villageCount, CancellationToken cancellationToken)
    {
        await EnsurePageForReadAsync($"{Paths.BuildBySlot(buildingSlotId)}&s=4", "expansion slots", cancellationToken);
        var counts = await _page.EvaluateAsync<int[]>(
            """
            () => [
              document.querySelectorAll('#build .usedExpansionSlot').length,
              document.querySelectorAll('#build .freeExpansionSlot').length
            ]
            """).WaitAsync(cancellationToken);
        if (counts is not [var used, var free] || used + free == 0)
        {
            var fallback = Math.Max(0, villageCount - 1);
            Notify($"[expansion] expansion tab showed no slots; assuming {fallback} used slot(s) from the village count.");
            return fallback;
        }

        Notify($"[expansion] expansion tab: used={used}, free={free}.");
        return used;
    }

    /// <summary>
    /// Seconds to wait until the account's culture points open another settlement slot, or null when a slot
    /// is free or the culture-point tab cannot be read (the founding form then decides).
    /// </summary>
    private async Task<int?> ReadCulturePointsWaitSecondsAsync(CancellationToken cancellationToken)
    {
        var status = await ReadBuildingsStatusAsync(cancellationToken);
        if (FindExpansionBuilding(status.Buildings) is not var (building, _))
        {
            Notify("[expansion] no Residence/Palace in this village; culture points not checked.");
            return null;
        }

        await EnsurePageForReadAsync($"{Paths.BuildBySlot(building.SlotId!.Value)}&s=2", "culture points", cancellationToken);
        var texts = await _page.EvaluateAsync<string[]?>(
            """
            () => {
              const root = document.querySelector('#build #expansionSettlements');
              if (!root) return null;
              return [
                root.querySelector('table.slotDetails tfoot td')?.textContent ?? '',
                root.querySelector('.barCaption')?.textContent ?? '',
                document.querySelector('#build table.culturePointsProductionDetails tfoot td')?.textContent ?? ''
              ];
            }
            """).WaitAsync(cancellationToken);
        if (texts is not [var slotsText, var captionText, var productionText]
            || ExpansionCalculator.ParseSettlementSlots(slotsText) is not var (used, total))
        {
            Notify("[expansion] culture-point tab not readable; continuing to the founding form.");
            return null;
        }

        if (used < total)
        {
            Notify($"[expansion] settlement slots used={used}/{total}; a slot is free.");
            return null;
        }

        var progress = ExpansionCalculator.ParseCulturePointsProgress(captionText);
        var perDay = ExpansionCalculator.ParseWholeNumber(productionText) ?? 0;
        var seconds = progress is var (current, needed)
            ? ExpansionCalculator.SecondsUntilCulturePoints(current, needed, perDay)
            : null;
        var wait = Math.Clamp(seconds ?? 3600, 300, 3600);
        Notify($"[expansion] settlement slots used={used}/{total}; culture points {progress?.Current.ToString(CultureInfo.InvariantCulture) ?? "?"}/{progress?.Needed.ToString(CultureInfo.InvariantCulture) ?? "?"} at {perDay.ToString(CultureInfo.InvariantCulture)}/day, next slot in ~{seconds?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}s; waiting {wait}s.");
        return wait;
    }

    private async Task<int> EstimateSettlerTrainingWaitSecondsAsync(CancellationToken cancellationToken)
    {
        const int fallbackSeconds = 1800;
        var status = await ReadBuildingsStatusAsync(cancellationToken);
        if (FindExpansionBuilding(status.Buildings) is not var (building, _))
        {
            return fallbackSeconds;
        }

        await GotoAsync($"{Paths.BuildBySlot(building.SlotId!.Value)}&s=1", cancellationToken);
        var queue = await ReadTroopTrainingQueueFromCurrentPageAsync(cancellationToken);
        var seconds = SettlerTrainingSecondsLeft(queue);
        Notify($"[expansion] settlers in training finish in {seconds?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}s.");
        return seconds is int value ? Math.Clamp(value + 30, 60, 3600) : fallbackSeconds;
    }

    /// <summary>The longest remaining time of the settler rows; each row's time already includes the rows before it.</summary>
    internal static int? SettlerTrainingSecondsLeft(IReadOnlyList<BuildQueueItem> queue)
    {
        int? longest = null;
        foreach (var item in queue)
        {
            if (CountSettlersInQueue([item]) == 0)
            {
                continue;
            }

            var match = Regex.Match(item.TimeLeft ?? item.Text ?? string.Empty, @"(\d+):(\d{2}):(\d{2})");
            if (match.Success)
            {
                var seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                longest = Math.Max(longest ?? 0, seconds);
            }
        }

        return longest;
    }

    private async Task<int?> ResolveOwnTribeIdAsync(CancellationToken cancellationToken)
    {
        var (tribe, _, _) = await GetCachedTribeAndPlusAsync(cancellationToken);
        foreach (var id in new[] { 1, 2, 3, 6, 7, 8 })
        {
            if (string.Equals(TroopCatalog.ResolveTribeFromTravianId(id), tribe, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        Notify($"[expansion] own tribe '{tribe}' has no Travian tribe id.");
        return null;
    }

    private static (Building Building, ExpansionBuildingType Type)? FindExpansionBuilding(IReadOnlyList<Building> buildings)
    {
        foreach (var (type, name) in ExpansionBuildings)
        {
            var match = buildings.FirstOrDefault(building => building.SlotId is > 0
                && (building.Gid == (int)type || string.Equals(building.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return (match, type);
            }
        }

        return null;
    }

    private async Task<int> ReadSettlersAtHomeAsync(CancellationToken cancellationToken)
    {
        await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: true);
        var count = await ReadAvailableTroopCountAsync($"t{SettlerSlot}", cancellationToken);
        Notify($"[expansion] settlers at home: {count?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
        return (int)Math.Max(0, count ?? 0);
    }

    internal static int CountSettlersInQueue(IReadOnlyList<BuildQueueItem> queue)
    {
        var total = 0;
        foreach (var item in queue)
        {
            var match = Regex.Match(item.Text ?? string.Empty, @"(\d+)\s*(?:Settlers?|Siedler)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                total += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return total;
    }

    private static int MaxAffordable(IReadOnlyDictionary<string, long> resources, TroopUnitBuildInfo info)
    {
        static long Per(long stock, int cost) => cost > 0 ? stock / cost : long.MaxValue;

        var max = new[]
        {
            Per(resources.GetValueOrDefault("wood"), info.WoodCost),
            Per(resources.GetValueOrDefault("clay"), info.ClayCost),
            Per(resources.GetValueOrDefault("iron"), info.IronCost),
            Per(resources.GetValueOrDefault("crop"), info.CropCost),
        }.Min();
        return (int)Math.Clamp(max, 0, int.MaxValue);
    }

    private async Task<ILocator?> FindFoundVillageLinkAsync()
    {
        var candidates = new[]
        {
            "a[href*='eventType=10']",
            "a:has-text('Found new village')",
            "button:has-text('Found new village')",
            "a:has-text('found new village')",
        };
        foreach (var selector in candidates)
        {
            var locator = _page.Locator(selector).First;
            if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync())
            {
                Notify($"[expansion] found-village action matched '{selector}'.");
                return locator;
            }
        }

        return null;
    }

    private async Task LogPageLinksForDiagnosisAsync(string stage)
    {
        try
        {
            var summary = await _page.EvaluateAsync<string>(
                """
                () => [...document.querySelectorAll('#content a, #content button')]
                  .map(e => `${(e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40)} -> ${e.getAttribute('href') || e.id || e.className}`)
                  .filter(t => t.length > 4).slice(0, 40).join(' | ')
                """);
            Notify($"[expansion] diagnosis ({stage}) url='{_page.Url}': {summary}");
        }
        catch (PlaywrightException ex)
        {
            Notify($"[expansion] diagnosis ({stage}) failed: {ex.Message}");
        }
    }
}
