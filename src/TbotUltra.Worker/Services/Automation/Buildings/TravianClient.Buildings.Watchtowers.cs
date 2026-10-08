using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<WatchtowerStatus?> ReadWatchtowerStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var identity = await ReadCurrentVillageIdentityAsync(cancellationToken);
        if (!forceRefresh
            && identity.Key is not null
            && _session.WatchtowerStatuses.TryGetValue(identity.Key, out var cached))
        {
            return cached;
        }

        if (!forceRefresh
            && identity.Key is not null
            && !_session.WatchtowerStatuses.ContainsKey(identity.Key)
            && _session.WatchtowerStatusReadAt.TryGetValue(identity.Key, out var lastReadAt)
            && DateTimeOffset.UtcNow - lastReadAt < TimeSpan.FromMinutes(15))
        {
            return null;
        }

        if (ResolveCityStatus(identity) != CityStatus.City)
        {
            return null;
        }

        await GotoAsync(Paths.BuildBySlot(40), cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var parsed = WatchtowerDomParser.Parse(await _page.ContentAsync(), CurrentTravianServerTimeUtc());
        if (identity.Key is not null)
        {
            _session.WatchtowerStatusReadAt[identity.Key] = DateTimeOffset.UtcNow;
        }
        if (!parsed.ExtensionAvailable || parsed.Status is null)
        {
            Notify($"[watchtower] status unavailable village='{identity.Name ?? "unknown"}': {parsed.BlockingMessage}");
            return null;
        }

        CacheWatchtowerStatus(identity, parsed.Status);
        Notify(
            $"[watchtower] village='{identity.Name ?? "unknown"}' level={parsed.Status.Level} " +
            $"projected={parsed.Status.ProjectedLevel} queue={parsed.Status.Active.Count}/2 source=wall");
        return parsed.Status;
    }

    public async Task<string> UpgradeWatchtowersToLevelAsync(
        int targetLevel,
        CancellationToken cancellationToken = default)
    {
        targetLevel = Math.Clamp(targetLevel, 1, 20);
        if (KnownCityCapability != CityCapability.Enabled)
        {
            throw new TaskWaitException(
                300,
                $"Watchtowers are dormant: City Capability is {KnownCityCapability}. The task is retained and will resume after a verified login reports Cities enabled.",
                "watchtower_prerequisite");
        }

        var identity = await ReadCurrentVillageIdentityAsync(cancellationToken);
        var cityStatus = ResolveCityStatus(identity);
        if (cityStatus != CityStatus.City)
        {
            throw new TaskWaitException(
                300,
                $"Watchtowers are dormant: village '{identity.Name ?? "unknown"}' is {cityStatus}, not a confirmed City. The task is retained and will resume after a village scan confirms City status.",
                "watchtower_prerequisite");
        }

        await GotoAsync(Paths.BuildBySlot(40), cancellationToken);
        await EnsureLoggedInAsync(cancellationToken: cancellationToken);
        var page = WatchtowerDomParser.Parse(await _page.ContentAsync(), CurrentTravianServerTimeUtc());
        if (!page.ExtensionAvailable || page.Status is null)
        {
            throw new TaskWaitException(
                300,
                $"Watchtowers are dormant because the live wall page did not expose the City extension. {page.BlockingMessage}",
                "watchtower_prerequisite");
        }

        CacheWatchtowerStatus(identity, page.Status);
        if (page.Status.ProjectedLevel >= targetLevel)
        {
            return $"Watchtowers already satisfy target level {targetLevel} (current={page.Status.Level}, projected={page.Status.ProjectedLevel}).";
        }

        if (page.Status.QueueFull || (!page.UpgradeActionAvailable && page.Status.Active.Count > 0))
        {
            var wait = ResolveWatchtowerWaitSeconds(page.Status);
            throw new TaskWaitException(
                wait,
                $"Watchtower queue is full ({page.Status.Active.Count}/2); next validation in {wait}s.");
        }

        if (!page.UpgradeActionAvailable)
        {
            var reason = string.IsNullOrWhiteSpace(page.BlockingMessage)
                ? "The live wall page did not expose an actionable Watchtower upgrade button."
                : page.BlockingMessage;
            throw new TaskWaitException(1800, $"Watchtower upgrade is temporarily unavailable: {reason}");
        }

        var queuedThisRun = 0;
        while (page.Status.ProjectedLevel < targetLevel
            && !page.Status.QueueFull
            && page.UpgradeActionAvailable)
        {
            var upgradeButton = _page.Locator(
                ".extension button.green[onclick*='action=build'], .extension button.green[value='Upgrade']").First;
            if (await upgradeButton.CountAsync() == 0 || !await upgradeButton.IsVisibleAsync())
            {
                throw new TaskWaitException(1800, "Watchtower upgrade button disappeared before the click; retrying later.");
            }

            var previousProjectedLevel = page.Status.ProjectedLevel;
            await ApplyActionDelayAsync(cancellationToken);
            await ClickLocatorAsync(upgradeButton, "watchtower-upgrade", cancellationToken);
            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                // The following live parse is the authoritative confirmation.
            }

            var confirmed = WatchtowerDomParser.Parse(await _page.ContentAsync(), CurrentTravianServerTimeUtc());
            if (confirmed.Status is null || confirmed.Status.ProjectedLevel <= previousProjectedLevel)
            {
                throw new InvalidOperationException(
                    $"Watchtower click was not confirmed by the live wall queue (before={previousProjectedLevel}).");
            }

            page = confirmed;
            queuedThisRun++;
            CacheWatchtowerStatus(identity, page.Status);
        }

        var delay = ResolveWatchtowerWaitSeconds(page.Status);
        throw new TaskWaitException(
            delay,
            $"Watchtowers queued {queuedThisRun} level(s); projected={page.Status.ProjectedLevel}, target={targetLevel}, queue={page.Status.Active.Count}/2; next validation in {delay}s.",
            TaskWaitReasons.WorkQueued);
    }

    internal static WatchtowerPageSnapshot ParseWatchtowerHtmlForTests(string html, DateTimeOffset observedAtUtc)
        => WatchtowerDomParser.Parse(html, observedAtUtc);

    private async Task<(string? Key, string? Name, int? X, int? Y)> ReadCurrentVillageIdentityAsync(
        CancellationToken cancellationToken)
    {
        var name = await TryReadActiveVillageNameSafeAsync(cancellationToken);
        var coords = await TryReadActiveVillageCoordsFromCurrentPageAsync(cancellationToken);
        var key = coords.X.HasValue && coords.Y.HasValue
            ? $"xy:{coords.X.Value}|{coords.Y.Value}"
            : string.IsNullOrWhiteSpace(name)
                ? null
                : $"name:{name.Trim().ToLowerInvariant()}";
        return (key, name, coords.X, coords.Y);
    }

    private CityStatus ResolveCityStatus((string? Key, string? Name, int? X, int? Y) identity)
    {
        if (identity.Key is not null
            && _session.VillageCityStatuses.TryGetValue(identity.Key, out var cached))
        {
            return cached;
        }

        var village = _cachedVillages?.FirstOrDefault(item =>
            identity.X.HasValue && identity.Y.HasValue
                ? item.CoordX == identity.X && item.CoordY == identity.Y
                : VillageIdentityReconciler.IsSameName(item.Name, identity.Name));
        return village?.CityStatus ?? _lastBuildingOverviewCityStatus;
    }

    private WatchtowerStatus? TryGetCachedWatchtowerStatus(string? name, int? x, int? y)
    {
        var key = x.HasValue && y.HasValue
            ? $"xy:{x.Value}|{y.Value}"
            : string.IsNullOrWhiteSpace(name)
                ? null
                : $"name:{name.Trim().ToLowerInvariant()}";
        return key is not null && _session.WatchtowerStatuses.TryGetValue(key, out var status)
            ? status
            : null;
    }

    private void CacheWatchtowerStatus(
        (string? Key, string? Name, int? X, int? Y) identity,
        WatchtowerStatus status)
    {
        if (identity.Key is not null)
        {
            _session.WatchtowerStatuses[identity.Key] = status;
            _session.WatchtowerStatusReadAt[identity.Key] = DateTimeOffset.UtcNow;
            try
            {
                _watchtowerSnapshotStore.Save(_account.Name, ServerUrl, identity.Key, status);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notify($"[watchtower-cache] could not persist village='{identity.Name ?? identity.Key}': {ex.Message}");
            }
        }
    }

    private static int ResolveWatchtowerWaitSeconds(WatchtowerStatus status)
    {
        var shortest = status.Active
            .Select(item => item.Finish?.RemainingSecondsAt(DateTimeOffset.UtcNow) ?? item.TimeLeftSeconds)
            .Where(seconds => seconds is > 0)
            .Select(seconds => seconds!.Value)
            .DefaultIfEmpty(status.NextLevelBuildSeconds ?? 300)
            .Min();
        return Math.Max(30, shortest + 5);
    }

}
