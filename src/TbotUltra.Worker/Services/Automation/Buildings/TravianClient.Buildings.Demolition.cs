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

        const int safetyCap = 30;

        // One-shot: read dorf2 to get original level + main building slot id.
        await ReloadOrGotoAsync(Paths.Buildings, cancellationToken);

        var initialSlots = await ReadBuildingInfosAsync(cancellationToken);
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

        var originalLevel = initialInfo.Level;
        var targetBuildingName = string.IsNullOrWhiteSpace(initialInfo.BuildingName)
            ? $"slot {slotId}"
            : initialInfo.BuildingName;
        var demolitions = 0;
        var currentLevel = originalLevel;

        // Stay on the Main Building page across iterations — only reload there between steps.
        var mainBuildingPath = Paths.BuildBySlot(mainSlot.Value);

        for (var iter = 0; iter < safetyCap && currentLevel > targetLevel; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Select target + click demolish. TryStartDemolitionStepAsync navigates to the
            // main building page (or reloads it if already there), so each step starts fresh.
            var started = await TryStartDemolitionStepAsync(
                mainBuildingSlotId: mainSlot.Value,
                targetSlotId: slotId,
                targetBuildingName: targetBuildingName,
                cancellationToken);
            if (!started)
            {
                // The demolish form is hidden while a demolition is already running.
                // Wait for any in-progress demolition to finish, then retry once before giving up.
                var pending = await WaitForActiveDemolitionToFinishAsync(mainBuildingPath, cancellationToken);
                if (pending)
                {
                    started = await TryStartDemolitionStepAsync(
                        mainBuildingSlotId: mainSlot.Value,
                        targetSlotId: slotId,
                        targetBuildingName: targetBuildingName,
                        cancellationToken);
                }
            }

            if (!started)
            {
                return $"Slot {slotId}: could not start demolition (main building page didn't expose a demolish action). Steps: {demolitions}.";
            }

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded)
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Continue — the wait loop below copes with partial loads.
            }

            demolitions += 1;
            currentLevel -= 1;
            Notify($"Slot {slotId}: demolish step {demolitions} queued (was level {currentLevel + 1}). Waiting for it to complete.");

            // Wait for this demolition to actually complete (read its real countdown timer)
            // before reloading and starting the next level — otherwise the form is unavailable.
            await WaitForActiveDemolitionToFinishAsync(mainBuildingPath, cancellationToken);
        }

        return $"Demolished slot {slotId} from level {originalLevel} to {currentLevel} in {demolitions} step(s).";
    }

    private async Task<bool> TryStartDemolitionStepAsync(
        int mainBuildingSlotId,
        int targetSlotId,
        string targetBuildingName,
        CancellationToken cancellationToken)
    {
        await GotoAsync(Paths.BuildBySlot(mainBuildingSlotId), cancellationToken);

        var selected = await _page.EvaluateAsync<bool>(
            """
            (args) => {
              const slotId = Number(args.slotId);
              const normalized = (args.name || '').toLowerCase();
              const selectCandidates = [
                'select[name*="demolish" i]',
                'form[action*="build.php" i] select',
                '#build.gid15 select',
                '.demolish select',
                '#content select'
              ];

              const getCandidates = () => {
                const nodes = [];
                for (const selector of selectCandidates) {
                  for (const node of document.querySelectorAll(selector)) {
                    if (!nodes.includes(node)) nodes.push(node);
                  }
                }
                return nodes;
              };

              const selects = getCandidates();
              for (const select of selects) {
                const options = Array.from(select.options || []);
                const direct = options.find(option => Number(option.value) === slotId);
                if (direct) {
                  select.value = direct.value;
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }

                const byText = options.find(option => {
                  const text = (option.textContent || '').toLowerCase();
                  return text.includes(normalized) || text.includes(`(${slotId})`) || text.includes(` ${slotId} `);
                });
                if (byText) {
                  select.value = byText.value;
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                  return true;
                }
              }

              return false;
            }
            """,
            new { slotId = targetSlotId, name = targetBuildingName });

        if (!selected)
        {
            return false;
        }

        await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const clickables = Array.from(document.querySelectorAll('button, input[type="submit"], a'));
              const safe = clickables.filter(node => {
                const text = ((node.textContent || '') + ' ' + (node.getAttribute('value') || '') + ' ' + (node.getAttribute('title') || '')).toLowerCase();
                const cls = (node.className || '').toLowerCase();
                const id = (node.id || '').toLowerCase();
                const isDemolish = text.includes('demolish') || text.includes('abbrechen') || text.includes('riva') || text.includes('demoliera');
                const isGold = text.includes('gold') || text.includes('instant') || cls.includes('gold') || id.includes('gold');
                const disabled = node.hasAttribute('disabled') || cls.includes('disabled');
                return isDemolish && !isGold && !disabled;
              });

              if (!safe.length) return false;
              safe[0].click();
              return true;
            }
            """);
    }

    // Reads the remaining seconds of an in-progress demolition (or any build queue timer)
    // from the Main Building page. Travian countdown timers carry a `value` attribute with
    // the seconds remaining, which is far more reliable than parsing the displayed text.
    private async Task<int?> ReadActiveDemolitionSecondsOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const seconds = [];
              const pushTimer = (node) => {
                if (!node) return;
                const attr = node.getAttribute && node.getAttribute('value');
                const n = attr != null ? Number(attr) : NaN;
                if (Number.isFinite(n) && n > 0) seconds.push(n);
              };
              const containers = document.querySelectorAll(
                '.buildingList, #building_contract, .underConstruction, .demolish, #demolish, .boxes-contents, .content');
              for (const c of containers) {
                for (const t of c.querySelectorAll('.timer, [id^="timer"], [counting="down"]')) {
                  pushTimer(t);
                }
              }
              return JSON.stringify(seconds);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<int>()
            : JsonSerializer.Deserialize<List<int>>(rawJson) ?? new List<int>();
        if (raw.Count == 0)
        {
            return null;
        }

        // The longest timer represents the active demolition/construction we must outlast.
        return raw.Max();
    }

    // Polls the Main Building page until no demolition/build countdown remains.
    // Returns true if a demolition was actually in progress and we waited for it.
    private async Task<bool> WaitForActiveDemolitionToFinishAsync(string mainBuildingPath, CancellationToken cancellationToken)
    {
        const int maxTotalWaitSeconds = 20 * 60; // safety cap
        const int maxChunkSeconds = 30;
        var waitedSeconds = 0;
        var waitedAtLeastOnce = false;

        while (waitedSeconds < maxTotalWaitSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = await ReadActiveDemolitionSecondsOnCurrentPageAsync(cancellationToken);
            if (remaining is not > 0)
            {
                return waitedAtLeastOnce;
            }

            waitedAtLeastOnce = true;
            var chunk = Math.Min(remaining.Value, maxChunkSeconds);
            Notify($"Demolition/build in progress (~{remaining.Value}s remaining). Waiting {chunk}s.");
            await Task.Delay(chunk * 1000 + 500, cancellationToken);
            waitedSeconds += chunk + 1;

            await ReloadOrGotoAsync(mainBuildingPath, cancellationToken);
        }

        Notify("Stopped waiting for demolition: safety cap reached.");
        return waitedAtLeastOnce;
    }


}

