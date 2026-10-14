using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop.Services.Orchestration;

internal interface IAutomationQueueEligibilityPort
{
    string? GetVillageKey(QueueItem item);
    string? GetVillageName(QueueItem item);
}

internal sealed class AutomationQueueEligibility(
    VillageSettingsStore villageSettings,
    Func<bool?> goldClubAvailability,
    IAutomationQueueEligibilityPort port,
    Func<bool>? heroRaidingEnabled = null)
{
    internal bool IsAllowed(QueueItem item)
    {
        // Hero raiding is a global opt-in, independent of the selected village's Auto or Hero group.
        if (string.Equals(item.TaskName, "hero_raid", StringComparison.OrdinalIgnoreCase))
            return heroRaidingEnabled?.Invoke() == true;

        // Farm lists and their Rally Point access are account-wide. The worker opens the farm page
        // from the current village and falls back to another owned Rally Point when needed.
        if (string.Equals(item.TaskName, "send_farmlists", StringComparison.OrdinalIgnoreCase))
        {
            return IsGroupEnabled(villageKey: null, QueueGroup.Farming);
        }

        var villageKey = port.GetVillageKey(item);
        if (item.Group == QueueGroup.Account)
        {
            return true;
        }

        var villageEnabled = villageKey is null
            || villageSettings.IsEnabledByKey(villageKey, defaultIfUnknown: true);
        return villageEnabled && IsGroupEnabledForItem(item, villageKey);
    }

    internal bool IsGroupEnabled(string? villageKey, QueueGroup group)
    {
        if (group == QueueGroup.Account)
        {
            return true;
        }

        if (group == QueueGroup.Farming && goldClubAvailability() != true)
        {
            return false;
        }

        if (villageKey is null)
        {
            return IsGroupEnabledForAnyVillage(group);
        }

        var groups = villageSettings.GetEnabledGroups(villageKey)
            ?? VillageSettingsStore.DefaultEnabledGroups;
        return groups.Contains(QueueGroupCatalog.GetKey(group), StringComparer.OrdinalIgnoreCase);
    }

    private bool IsGroupEnabledForItem(QueueItem item, string? villageKey)
    {
        if (item.Group == QueueGroup.Demolish)
        {
            return true;
        }

        if (item.Payload.TryGetValue(BotOptionPayloadKeys.AutoAddedBy, out var autoAddedBy)
            && string.Equals(
                autoAddedBy,
                BotOptionPayloadKeys.AutoAddedByHeroRallyPointRepair,
                StringComparison.OrdinalIgnoreCase))
        {
            return IsGroupEnabled(villageKey, QueueGroup.Hero);
        }

        return IsGroupEnabled(villageKey, item.Group);
    }

    private bool IsGroupEnabledForAnyVillage(QueueGroup group)
    {
        var key = QueueGroupCatalog.GetKey(group);
        foreach (var (_, enabledGroups) in villageSettings.GetEnabledVillagesGroups())
        {
            var effective = enabledGroups ?? VillageSettingsStore.DefaultEnabledGroups;
            if (effective.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
