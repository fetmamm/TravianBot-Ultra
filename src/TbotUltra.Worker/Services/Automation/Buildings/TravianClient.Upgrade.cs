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
    private async Task ClickDetectedUpgradeCandidateAsync(int slotId, int? candidateIndex, CancellationToken cancellationToken)
    {
        if (candidateIndex is null || candidateIndex < 0)
        {
            throw new InvalidOperationException($"Upgrade candidate index is missing for slot {slotId}.");
        }

        await EnsureExpectedBuildSlotPageAsync(slotId, "click detected upgrade candidate", cancellationToken);

        if (await TryClickOfficialPrimaryUpgradeButtonAsync(slotId, cancellationToken))
        {
            return;
        }

        await EnsureExpectedBuildSlotPageAsync(slotId, "click detected upgrade candidate fallback", cancellationToken);
        var locator = _page.Locator("button, input[type='submit'], input[type='button'], a, div.addHoverClick, div.button-container").Nth(candidateIndex.Value);
        await RetryAsync($"click detected upgrade candidate index {candidateIndex.Value} for slot {slotId}", async () =>
        {
            await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
            await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
        }, cancellationToken: cancellationToken);

        await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking detected upgrade candidate.", cancellationToken);
    }

    private async Task<bool> TryClickOfficialPrimaryUpgradeButtonAsync(int slotId, CancellationToken cancellationToken)
    {
        var pattern = new Regex(@"upgrade\s+to\s+level", RegexOptions.IgnoreCase);
        var selectors = new[]
        {
            ".upgradeButtonsContainer .section1 button.green.build",
            ".upgradeButtonsContainer .section1 button.green",
            ".upgradeButtonsContainer .section1 button",
        };

        foreach (var selector in selectors)
        {
            var locator = _page.Locator(selector).Filter(new LocatorFilterOptions { HasTextRegex = pattern }).First;
            if (await locator.CountAsync() <= 0)
            {
                continue;
            }

            try
            {
                await RetryAsync($"click official primary upgrade button '{selector}'", async () =>
                {
                    await DelayBeforeClickAsync(cancellationToken); // Action pacing "Click" delay
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                }, cancellationToken: cancellationToken);
                await PauseForManualStepIfVisibleAsync("Manual verification appeared after clicking official upgrade button.", cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                if (TravianUrls.ExtractSlotIdFromUrl(_page.Url) != slotId)
                {
                    Notify($"[upgrade-click:verbose] official primary button '{selector}' left slot {slotId}; verifying progress. Last click status: {ex.Message}");
                    return true;
                }

                Notify($"[upgrade-click:verbose] official primary button '{selector}' did not confirm click: {ex.Message}");
            }
        }

        return false;
    }

    private async Task<int?> ReadUpgradeDurationSecondsOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading upgrade duration.", cancellationToken);
        var rawJson = await _page.EvaluateAsync<string>(
            """
            () => {
              const clean = (value) => (value || '').replace(/\s+/g, ' ').trim();
              const isDuration = (value) => /\d{1,3}\s*:\s*\d{1,2}(?:\s*:\s*\d{1,2})?/.test(value) || /\b\d+\s*(?:min|minute|sec|second)s?\b/i.test(value);
              const found = [];
              const seen = new Set();
              const pushIfDuration = (value) => {
                const text = clean(value);
                if (!text || !isDuration(text) || seen.has(text)) return;
                seen.add(text);
                found.push(text);
              };

              // Prefer explicit upgrade duration element (same area as the upgrade button).
              const directSelectors = [
                '.inlineIcon.duration .value',
                '.inlineIcon.duration .timer',
                '.inlineIcon.duration'
              ];
              for (const selector of directSelectors) {
                for (const node of document.querySelectorAll(selector)) {
                  pushIfDuration(node.textContent);
                }
              }

              const blocks = [
                ...document.querySelectorAll('.upgradeBuilding, .contract, .contractWrapper, .build_details, #contract, form[action*="build.php"]')
              ];
              for (const block of blocks) {
                const nodes = block.querySelectorAll('.timer, .countdown, .value, [counting="down"], [id^="timer"]');
                for (const node of nodes) {
                  pushIfDuration(node.textContent);
                }
              }

              return JSON.stringify(found);
            }
            """);

        var raw = string.IsNullOrWhiteSpace(rawJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(rawJson) ?? new List<string>();
        if (raw.Count == 0)
        {
            return null;
        }

        var candidateSeconds = raw
            .Select(TravianParsing.ParseDurationToSeconds)
            .Where(value => value.HasValue && value.Value > 0)
            .Select(value => value!.Value)
            .ToList();
        if (candidateSeconds.Count == 0)
        {
            return null;
        }

        // Prefer the shortest detected upgrade timer; first-hit can be an unrelated countdown.
        return candidateSeconds.Min();
    }

    // Reads the population increase the current build/upgrade page will grant, from the
    // ".culturePointsAndPopulation" panel: the ".unit" whose icon is "population_medium" carries
    // a ".delta" like "(+3)". Returns null when the panel/value is absent (e.g. at max level).
    private async Task<int?> ReadUpgradePopulationDeltaOnCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? deltaText;
        try
        {
            deltaText = await _page.EvaluateAsync<string?>(
                """
                () => {
                  const units = document.querySelectorAll('.culturePointsAndPopulation .unit, .buildingBenefits .unit');
                  for (const unit of units) {
                    if (unit.querySelector('i.population_medium')) {
                      const delta = unit.querySelector('.delta');
                      return delta ? delta.textContent : null;
                    }
                  }
                  return null;
                }
                """);
        }
        catch (Exception ex)
        {
            Notify($"[ReadUpgradePopulationDeltaOnCurrentPageAsync] read failed: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(deltaText))
        {
            return null;
        }

        // Strip everything but sign and digits (the markup carries invisible bidi marks).
        var cleaned = new string(deltaText.Where(c => char.IsDigit(c) || c == '-').ToArray());
        if (string.IsNullOrEmpty(cleaned) || cleaned == "-")
        {
            return null;
        }

        return int.TryParse(cleaned, out var value) ? value : null;
    }

    // Adds a population delta to the active village in the cache so the UI village list reflects a
    // just-queued upgrade. The incremental add needs an existing baseline; if it is missing, skip the
    // cache update instead of navigating away from the build flow just to seed UI-only data.
    private async Task AddPopulationToActiveVillageCacheAsync(int delta, CancellationToken cancellationToken)
    {
        if (delta == 0 || _cachedVillages is not { Count: > 0 })
        {
            return;
        }

        string activeName;
        try
        {
            activeName = await ReadActiveVillageNameAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Notify($"[AddPopulationToActiveVillageCacheAsync] could not read active village: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(activeName))
        {
            return;
        }

        var hasBaseline = _cachedVillages.Any(v =>
            string.Equals(v.Name, activeName, StringComparison.Ordinal) && v.Population.HasValue);
        if (!hasBaseline)
        {
            Notify($"[population] no baseline for '{activeName}', skipping UI cache population delta to avoid extra navigation.");
            return;
        }

        var updated = false;
        var list = _cachedVillages.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, activeName, StringComparison.Ordinal)
                && list[i].Population is int currentPop)
            {
                list[i] = list[i] with { Population = currentPop + delta };
                updated = true;
                break;
            }
        }

        if (updated)
        {
            _cachedVillages = list;
            Notify($"[population] active village '{activeName}' population +{delta} (UI cache updated).");
            // Push the new population to the UI immediately (bypass the 20s ui-sync throttle) so the
            // "Villages" box reflects each upgrade as it completes. Cheap: reads the current page only.
            await TryEmitUiSyncSnapshotAsync(cancellationToken, force: true);
        }
    }

    internal enum UpgradeAttemptOutcome
    {
        CanUpgrade = 0,
        BlockedByResources = 1,
        BlockedByQueue = 2,
        BlockedByMaxLevel = 3,
        BlockedUnknown = 4,
    }

    private sealed record UpgradeAttemptResult(
        UpgradeAttemptOutcome Outcome,
        string Reason,
        int? DetectedMaxLevel,
        int? QueueWaitSeconds,
        int? CandidateIndex,
        string DebugSummary);

    private sealed record UpgradeProgressResult(
        bool Advanced,
        bool QueuedOrInProgress,
        string Evidence);

}