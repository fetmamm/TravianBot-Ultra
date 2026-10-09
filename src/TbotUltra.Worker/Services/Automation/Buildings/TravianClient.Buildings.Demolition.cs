using Microsoft.Playwright;
using System.Globalization;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

// Building surface of the TravianClient facade. The interface list is declared
// on this partial to co-locate the contract with the domain it covers.
public sealed partial class TravianClient : IBuildingClient
{

    public async Task<string> DemolishBuildingToLevelAsync(
        string targetBuildingSlotOrName,
        int targetLevel,
        CancellationToken cancellationToken = default)
    {
        Notify($"[demolish] starting — target='{targetBuildingSlotOrName}', targetLevel={targetLevel}");
        if (targetLevel < 0)
        {
            throw new InvalidOperationException("Demolish target level must be >= 0.");
        }

        if (!int.TryParse(targetBuildingSlotOrName.Trim(), out var slotId))
        {
            throw new InvalidOperationException($"Demolish requires a numeric slot id, got '{targetBuildingSlotOrName}'.");
        }
        if (slotId < 19)
        {
            throw new InvalidOperationException($"Demolish slot {slotId} is outside the building range.");
        }

        // One-shot: read dorf2 to get the live target level and Main Building slot.
        await OpenVillageOverviewAsync(resourceFields: false, cancellationToken);

        var initialSlots = (await ReadBuildingInfosAsync(cancellationToken)).Buildings;
        if (!initialSlots.TryGetValue(slotId, out var initialInfo) || initialInfo.Level <= 0)
        {
            return $"Slot {slotId}: nothing to demolish (already empty).";
        }
        if (initialInfo.Level <= targetLevel)
        {
            return $"Slot {slotId}: already at level {initialInfo.Level} (target {targetLevel}).";
        }

        var mainSlot = initialSlots
            .Where(kvp => ParseGidFromBuildingCode(kvp.Value.BuildingCode) == 15)
            .OrderByDescending(kvp => kvp.Value.Level)
            .Select(kvp => (int?)kvp.Key)
            .FirstOrDefault();
        if (mainSlot is null)
        {
            throw new InvalidOperationException("Demolition requires Main Building.");
        }

        var targetBuildingName = string.IsNullOrWhiteSpace(initialInfo.BuildingName)
            ? $"slot {slotId}"
            : initialInfo.BuildingName;
        var mainBuildingPath = Paths.BuildBySlot(mainSlot.Value);
        await OpenSlotFromOverviewAsync(mainSlot.Value, cancellationToken);

        var activeSeconds = await ReadActiveDemolitionSecondsOnCurrentPageAsync(cancellationToken);
        if (activeSeconds is > 0)
        {
            return $"Demolition already running for {activeSeconds.Value}s. queue_wait_seconds={activeSeconds.Value}";
        }

        var started = await TryStartDemolitionStepAsync(
            targetSlotId: slotId,
            cancellationToken);
        if (!started)
        {
            return $"Slot {slotId}: could not start demolition (Main Building did not expose the Official demolish form).";
        }

        // The Official form submission navigates back to the Main Building page. Wait for that
        // navigation before reading the authoritative server countdown from the returned page.
        await WaitForPageReadyAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        activeSeconds = await ReadActiveDemolitionSecondsOnCurrentPageAsync(cancellationToken);
        if (activeSeconds is not > 0)
        {
            await ReloadOrGotoAsync(mainBuildingPath, cancellationToken);
            activeSeconds = await ReadActiveDemolitionSecondsOnCurrentPageAsync(cancellationToken);
        }

        if (activeSeconds is not > 0)
        {
            return $"Slot {slotId}: demolish click was not confirmed by an active demolition timer.";
        }

        Notify($"Slot {slotId}: started one demolish step for {targetBuildingName}; server timer {activeSeconds.Value}s.");
        return $"Started demolition for {targetBuildingName}. queue_wait_seconds={activeSeconds.Value}";
    }

    private async Task<bool> TryStartDemolitionStepAsync(
        int targetSlotId,
        CancellationToken cancellationToken)
    {
        var selected = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const select = document.querySelector('form.demolish_building select#demolish[name="abriss"]');
              const option = select && Array.from(select.options).find(item => Number(item.value) === Number(args.slotId));
              if (!select || !option) return false;
              select.value = option.value;
              select.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """,
            new { slotId = targetSlotId });

        if (!selected)
        {
            return false;
        }

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        // Stop demolition can cancel this narrow action scope while the page is being prepared.
        // Do the final check immediately before the state-changing Official click.
        cancellationToken.ThrowIfCancellationRequested();
        var button = _page.Locator(
            "form.demolish_building button.textButtonV1.green[value='Demolish']").First;
        if (await button.CountAsync() == 0
            || !await button.IsVisibleAsync()
            || !await button.IsEnabledAsync())
        {
            return false;
        }

        try
        {
            await button.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs })
                .WaitAsync(cancellationToken);
            return true;
        }
        catch (PlaywrightException ex) when (
            !BrowserFailureClassifier.IsTargetCrash(ex)
            && IsTransientExecutionContextError(ex))
        {
            // The click can destroy its own JavaScript context while Travian submits the form.
            // Treat only that navigation transition as attempted; the timer read below remains
            // the authoritative confirmation that the demolition actually started.
            Notify("[demolish:verbose] submit navigation replaced the click context; verifying the server timer on the returned page.");
            return true;
        }
    }

    // The active Official demolition row is rendered separately from the form as table#demolish.
    // Restricting the read to that table avoids the unrelated server-clock and construction timers.
    private async Task<int?> ReadActiveDemolitionSecondsOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _page.EvaluateAsync<int?>(
            """
            () => {
              const timer = document.querySelector('table#demolish .timer[value]');
              const seconds = timer ? Number(timer.getAttribute('value')) : NaN;
              return Number.isFinite(seconds) && seconds > 0 && seconds < 86400 ? Math.ceil(seconds) : null;
            }
            """);
    }


}

