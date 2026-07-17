using System.Globalization;
using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int ReinforcementFallbackCooldownSeconds = 300;

    public Task<string> SendReinforcementsBetweenOwnVillagesAsync(CancellationToken cancellationToken = default)
    {
        return SendReinforcementsBetweenOwnVillagesAsync(allowSameSourceAndTarget: false, cancellationToken);
    }

    public Task<string> TestSendReinforcementsBetweenOwnVillagesAsync(CancellationToken cancellationToken = default)
    {
        return SendReinforcementsBetweenOwnVillagesAsync(allowSameSourceAndTarget: true, cancellationToken);
    }

    private async Task<string> SendReinforcementsBetweenOwnVillagesAsync(
        bool allowSameSourceAndTarget,
        CancellationToken cancellationToken)
    {
        Notify($"[reinforce] starting — target='{(string.IsNullOrWhiteSpace(_config.ReinforcementsTargetVillageName) ? "(config)" : _config.ReinforcementsTargetVillageName)}', sameSrcTgtAllowed={allowSameSourceAndTarget}");
        await EnsureLoggedInAsync();

        if (ResolveEnabledReinforcementRules(_config).Count == 0)
        {
            return "Reinforcements require at least one enabled troop type.";
        }

        var villages = await ReadVillagesAsync(cancellationToken);
        var targetVillage = ResolveReinforcementTargetVillage(villages, allowSameSourceAndTarget);
        if (targetVillage is null)
        {
            return "Reinforcements require a target village.";
        }

        var sourceNames = ResolveReinforcementSourceNames(targetVillage, allowSameSourceAndTarget);
        if (sourceNames.Count == 0)
        {
            return "Reinforcements require at least one source village.";
        }

        var sentCount = 0;
        var skippedCount = 0;
        var noAvailableTroopsCount = 0;
        foreach (var sourceName in sourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allowSameSourceAndTarget && string.Equals(sourceName, targetVillage.Name, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                Notify($"[reinforce:verbose] skip '{sourceName}' — it is the target village");
                continue;
            }

            var sourceVillage = villages.FirstOrDefault(village =>
                string.Equals(village.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (sourceVillage is null)
            {
                skippedCount++;
                Notify($"[reinforce] skip — source village '{sourceName}' was not found");
                continue;
            }

            var troopRules = ResolveEnabledReinforcementRules(_config, sourceVillage.Name);
            if (troopRules.Count == 0)
            {
                skippedCount++;
                Notify($"[reinforce:verbose] skip '{sourceVillage.Name}' — no troops selected for this source");
                continue;
            }

            Notify($"[reinforce:verbose] opening Rally Point in '{sourceVillage.Name}'");
            await SwitchToVillageAsync(sourceVillage.Name, sourceVillage.Url, cancellationToken, skipFeatureRefresh: true);
            await EnsureRallyPointAndOpenSendTroopsPageAsync(cancellationToken, allowReuseCurrentPage: false);

            var tribe = await ReadActiveVillageTribeAsync(cancellationToken);
            if (!TroopCatalog.IsKnownTribe(tribe))
            {
                skippedCount++;
                Notify($"[tribe] reinforcement skipped for '{sourceVillage.Name}' because its village tribe is unknown.");
                continue;
            }

            var resolvedAmounts = await ResolveReinforcementAmountsAsync(tribe, troopRules, cancellationToken);
            if (resolvedAmounts.Count == 0)
            {
                skippedCount++;
                noAvailableTroopsCount++;
                Notify($"[reinforce:verbose] skip '{sourceVillage.Name}' — no selected troops available");
                continue;
            }

            var sendResult = await TrySendReinforcementAsync(targetVillage, resolvedAmounts, cancellationToken);
            if (!sendResult.Sent)
            {
                skippedCount++;
                Notify(string.IsNullOrWhiteSpace(sendResult.Error)
                    ? $"[reinforce] could not send from '{sourceVillage.Name}' to '{targetVillage.Name}'"
                    : $"[reinforce] could not send from '{sourceVillage.Name}' to '{targetVillage.Name}': {sendResult.Error}");
                if (allowSameSourceAndTarget
                    && string.Equals(sourceVillage.Name, targetVillage.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(sendResult.Error))
                {
                    return $"Reinforcements test stopped: {sendResult.Error}";
                }

                continue;
            }

            sentCount++;
            Notify($"[reinforce] sent from '{sourceVillage.Name}' to '{targetVillage.Name}' — {FormatReinforcementAmounts(resolvedAmounts)}");
            await ApplyActionDelayAsync(cancellationToken);
        }

        if (sentCount > 0)
        {
            return $"Reinforcements completed. Sent from {sentCount} source village(s), skipped {skippedCount}.";
        }

        if (noAvailableTroopsCount > 0)
        {
            return $"Reinforcements completed. No selected troops available in {noAvailableTroopsCount} source village(s), skipped {skippedCount}. Next automatic send uses the configured reinforcement interval.";
        }

        throw new InvalidOperationException($"Reinforcements had no shipment to send. queue_wait_seconds={ReinforcementFallbackCooldownSeconds}");
    }

    private Village? ResolveReinforcementTargetVillage(IReadOnlyList<Village> villages, bool allowFallback)
    {
        if (!string.IsNullOrWhiteSpace(_config.ReinforcementsTargetVillageName))
        {
            var configuredTarget = villages.FirstOrDefault(village =>
                string.Equals(village.Name, _config.ReinforcementsTargetVillageName, StringComparison.OrdinalIgnoreCase));
            if (configuredTarget is not null)
            {
                return configuredTarget;
            }

            if (!allowFallback)
            {
                throw new InvalidOperationException($"Reinforcement target village '{_config.ReinforcementsTargetVillageName}' was not found.");
            }
        }

        return allowFallback ? villages.FirstOrDefault() : null;
    }

    private IReadOnlyList<string> ResolveReinforcementSourceNames(Village targetVillage, bool allowFallback)
    {
        var sourceNames = (_config.ReinforcementsSourceVillageNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceNames.Count > 0 || !allowFallback)
        {
            return sourceNames;
        }

        sourceNames = (_config.ReinforcementsTroopRules ?? [])
            .Where(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.SourceVillageName))
            .Select(rule => rule.SourceVillageName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return sourceNames.Count > 0 ? sourceNames : [targetVillage.Name];
    }

    private static IReadOnlyList<ReinforcementTroopRule> ResolveEnabledReinforcementRules(
        TbotUltra.Core.Configuration.BotOptions options,
        string? sourceVillageName = null)
    {
        var rules = (options.ReinforcementsTroopRules ?? [])
            .Where(rule => rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.TroopType))
            .Select(rule => rule.Normalize())
            .ToList();

        if (!string.IsNullOrWhiteSpace(sourceVillageName))
        {
            var sourceRules = rules
                .Where(rule => string.Equals(rule.SourceVillageName, sourceVillageName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sourceRules.Count > 0)
            {
                return sourceRules
                    .GroupBy(rule => rule.TroopType, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            rules = rules
                .Where(rule => string.IsNullOrWhiteSpace(rule.SourceVillageName))
                .ToList();
        }

        return rules
            .GroupBy(rule => $"{rule.SourceVillageName}\u001f{rule.TroopType}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<IReadOnlyList<ResolvedReinforcementAmount>> ResolveReinforcementAmountsAsync(
        string tribe,
        IReadOnlyList<ReinforcementTroopRule> rules,
        CancellationToken cancellationToken)
    {
        var amounts = new List<ResolvedReinforcementAmount>();
        foreach (var rule in rules)
        {
            var troopIndex = TroopCatalog.ResolveTroopIndex(rule.TroopType);
            if (troopIndex is null)
            {
                Notify($"[reinforce:verbose] skip unknown troop type '{rule.TroopType}'");
                continue;
            }

            var troopTypes = TroopCatalog.ResolveTroopTypesForTribe(tribe);
            if (!troopTypes.Any(item => string.Equals(item, rule.TroopType, StringComparison.OrdinalIgnoreCase)))
            {
                Notify($"[reinforce:verbose] skip '{rule.TroopType}' — not valid for tribe '{tribe}'");
                continue;
            }

            var fieldToken = $"t{troopIndex.Value}";
            var available = await ReadAvailableTroopCountAsync(fieldToken, cancellationToken);
            var requestedAmount = ResolveRequestedReinforcementAmount(rule, available);
            if (requestedAmount <= 0)
            {
                Notify($"[reinforce:verbose] skip {rule.TroopType} — available={available.GetValueOrDefault()}");
                continue;
            }

            if (!rule.UsesAllAvailable && !rule.UsesPercentAvailable && available is not null && available.Value < requestedAmount)
            {
                Notify($"[reinforce:verbose] skip {rule.TroopType} — available={available.Value}, requested={requestedAmount}");
                continue;
            }

            amounts.Add(new ResolvedReinforcementAmount(rule.TroopType, troopIndex.Value, requestedAmount));
        }

        return amounts;
    }

    private static int ResolveRequestedReinforcementAmount(ReinforcementTroopRule rule, long? available)
    {
        if (rule.UsesAllAvailable)
        {
            return ClampLongToInt32(available) ?? 0;
        }

        if (rule.PercentAvailable is { } percent)
        {
            if (available is null || available.Value <= 0)
            {
                return 0;
            }

            var requested = (long)Math.Floor(available.Value * (percent / 100d));
            return ClampLongToInt32(Math.Max(1, requested)) ?? 0;
        }

        return rule.NormalizedAmount;
    }

    internal static string[] BuildTroopInputSelectors(string fieldToken) =>
    [
        $"input[name='troops[0][{fieldToken}]']",
        $"input[name='troop[{fieldToken}]']",
        $"input[name$='[{fieldToken}]']",
        $"input[name='{fieldToken}']",
        $"input[id$='{fieldToken}']",
    ];

    private async Task<ReinforcementSendAttemptResult> TrySendReinforcementAsync(
        Village targetVillage,
        IReadOnlyList<ResolvedReinforcementAmount> amounts,
        CancellationToken cancellationToken)
    {
        if (!await TryFillReinforcementTargetAsync(targetVillage, cancellationToken))
        {
            return new ReinforcementSendAttemptResult(false, "Could not fill target village.");
        }

        foreach (var amount in amounts)
        {
            if (!await TryFillTroopInputAsync($"t{amount.TroopIndex}", amount.TroopType, amount.Amount, cancellationToken))
            {
                Notify($"[reinforce:verbose] could not fill {amount.TroopType} amount");
                return new ReinforcementSendAttemptResult(false, $"Could not fill {amount.TroopType} amount.");
            }
        }

        if (!await TrySelectReinforcementModeAsync(cancellationToken))
        {
            return new ReinforcementSendAttemptResult(false, "Could not select reinforcement mode.");
        }

        await WaitBeforeReinforcementConfirmAsync("first confirm", cancellationToken);
        if (!await TryClickConfirmButtonAsync(cancellationToken))
        {
            return new ReinforcementSendAttemptResult(false, "Could not click first confirm.");
        }

        await WaitForPageReadyAsync(cancellationToken);
        if (!await WaitForManualAttackConfirmationPageAsync(cancellationToken))
        {
            var error = await ReadReinforcementFormErrorAsync(cancellationToken);
            return new ReinforcementSendAttemptResult(false, string.IsNullOrWhiteSpace(error) ? "Confirmation page did not load." : error);
        }

        await WaitBeforeReinforcementConfirmAsync("final confirm", cancellationToken);
        if (!await TryClickConfirmButtonAsync(cancellationToken))
        {
            return new ReinforcementSendAttemptResult(false, "Could not click final confirm.");
        }

        await WaitForManualAttackCompletionAsync(cancellationToken);
        await EnsureLoggedInAsync();
        return new ReinforcementSendAttemptResult(true, null);
    }

    private async Task<string?> ReadReinforcementFormErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _page.EvaluateAsync<string?>(
                """
                () => {
                  const node = document.querySelector('p.error, .error, .errorMessage, .message.error');
                  const text = (node?.textContent || '').replace(/\s+/g, ' ').trim();
                  return text || null;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return null;
        }
    }

    private async Task WaitBeforeReinforcementConfirmAsync(string stepName, CancellationToken cancellationToken)
    {
        Notify($"[reinforce:verbose] waiting before {stepName}");
        await WaitForPageReadyAsync(cancellationToken); // Wait for page to load
        await Task.Delay(500, cancellationToken);
    }

    private async Task<bool> TryFillReinforcementTargetAsync(Village targetVillage, CancellationToken cancellationToken)
    {
        var filled = await _page.EvaluateAsync<bool>(
            """
            ({ targetName, x, y }) => {
              const setValue = (el, value) => {
                if (!el) return false;
                el.focus();
                el.value = String(value);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              };
              const first = (selectors) => {
                for (const selector of selectors) {
                  const node = document.querySelector(selector);
                  if (node) return node;
                }
                return null;
              };

              if (Number.isFinite(x) && Number.isFinite(y)) {
                const xOk = setValue(first(['input[name="x"]', 'input[name="xCoord"]', 'input#xCoordInput', 'input[name*="x" i][type="text"]']), x);
                const yOk = setValue(first(['input[name="y"]', 'input[name="yCoord"]', 'input#yCoordInput', 'input[name*="y" i][type="text"]']), y);
                if (xOk && yOk) return true;
              }

              if (targetName) {
                return setValue(first(['input[name="dname"]', 'input[name="villageName"]', 'input[name*="village" i]', 'input[name*="name" i]']), targetName);
              }

              return false;
            }
            """,
            new
            {
                targetName = targetVillage.Name,
                x = targetVillage.CoordX,
                y = targetVillage.CoordY,
            });

        return filled;
    }

    private async Task<bool> TrySelectReinforcementModeAsync(CancellationToken cancellationToken)
    {
        return await _page.EvaluateAsync<bool>(
            """
            () => {
              const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
              const radioButtons = Array.from(document.querySelectorAll('input[type="radio"][name="eventType"]'));
              const radio = radioButtons.find(node => {
                const value = (node.getAttribute('value') || '').trim();
                const label = normalize(node.parentElement?.textContent || node.closest('label')?.textContent || '');
                return value === '5' || label.includes('reinforcement') || label.includes('support');
              });
              if (!radio) return false;
              radio.checked = true;
              radio.dispatchEvent(new Event('input', { bubbles: true }));
              radio.dispatchEvent(new Event('change', { bubbles: true }));
              return true;
            }
            """);
    }

    private static string FormatReinforcementAmounts(IReadOnlyList<ResolvedReinforcementAmount> amounts)
    {
        return string.Join(", ", amounts.Select(amount =>
            $"{amount.Amount.ToString(CultureInfo.InvariantCulture)} {amount.TroopType}"));
    }

    private sealed record ResolvedReinforcementAmount(string TroopType, int TroopIndex, int Amount);
    private sealed record ReinforcementSendAttemptResult(bool Sent, string? Error);
}
