using System.Text.Json;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const int BreweryCelebrationRetrySeconds = 60;

    public async Task<BreweryCelebrationStatus> ReadBreweryCelebrationStatusAsync(
        IReadOnlyList<Building>? knownBuildings = null,
        CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        var tribe = await ReadTribeAsync(cancellationToken);
        if (!string.Equals(tribe, "Teutons", StringComparison.OrdinalIgnoreCase))
        {
            return new BreweryCelebrationStatus(
                false,
                null,
                false,
                null,
                false,
                null,
                "N/A",
                "Teutons only.");
        }

        var activeVillage = await ReadActiveVillageNameAsync(cancellationToken);
        var isCapital = TryGetCachedCapitalState(activeVillage);
        if (!isCapital.HasValue)
        {
            await RefreshCapitalStateForActiveVillageAsync(cancellationToken);
            isCapital = TryGetCachedCapitalState(activeVillage);
        }

        var buildings = knownBuildings ?? (await ReadBuildingsStatusAsync(cancellationToken)).Buildings;
        var brewery = ResolveBreweryBuilding(buildings);
        if (brewery is null || brewery.SlotId is not > 0)
        {
            return new BreweryCelebrationStatus(
                true,
                isCapital,
                false,
                null,
                false,
                null,
                "N/A",
                "Brewery not found.");
        }

        if (isCapital == false)
        {
            return new BreweryCelebrationStatus(
                true,
                false,
                true,
                brewery.SlotId,
                false,
                null,
                "N/A",
                "Capital village required.");
        }

        await GotoAsync(Paths.BuildBySlot(brewery.SlotId.Value), cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared while reading brewery celebration status.", cancellationToken);
        await EnsureLoggedInAsync();

        var pageStatus = await ReadBreweryCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = ParseDurationToSeconds(pageStatus.RemainingText);
        return new BreweryCelebrationStatus(
            true,
            isCapital,
            true,
            brewery.SlotId,
            pageStatus.CelebrationRunning,
            remainingSeconds,
            remainingSeconds is > 0 ? FormatDuration(remainingSeconds.Value) : (pageStatus.CanStart ? "Ready" : "N/A"),
            pageStatus.StatusText);
    }

    public async Task<string> RunBreweryCelebrationAsync(CancellationToken cancellationToken = default)
    {
        LogFunctionStarted();
        await EnsureLoggedInAsync();

        var status = await ReadBreweryCelebrationStatusAsync(cancellationToken: cancellationToken);
        if (!status.IsAvailableForTribe)
        {
            return "Brewery celebration: Teutons only. queue_wait_seconds=600";
        }

        if (status.IsCapital == false)
        {
            return "Brewery celebration: capital village required. queue_wait_seconds=600";
        }

        if (!status.BreweryExists || status.BrewerySlotId is not > 0)
        {
            return "Brewery celebration: brewery not found in this village. queue_wait_seconds=600";
        }

        if (status.CelebrationRunning && status.RemainingSeconds is > 0)
        {
            return $"Brewery celebration running. queue_wait_seconds={Math.Max(1, status.RemainingSeconds.Value)}";
        }

        var startAttempt = await TryStartBreweryCelebrationFromCurrentPageAsync(cancellationToken);
        if (!startAttempt.Started)
        {
            return $"{startAttempt.Message} queue_wait_seconds={BreweryCelebrationRetrySeconds}";
        }

        await Task.Delay(500, cancellationToken);
        await PauseForManualStepIfVisibleAsync("Manual verification appeared after starting brewery celebration.", cancellationToken);
        await EnsureLoggedInAsync();

        var startedStatus = await ReadBreweryCelebrationStatusFromCurrentPageAsync(cancellationToken);
        var remainingSeconds = ParseDurationToSeconds(startedStatus.RemainingText) ?? BreweryCelebrationRetrySeconds;
        return $"Brewery celebration started. queue_wait_seconds={Math.Max(1, remainingSeconds)}";
    }

    private static Building? ResolveBreweryBuilding(IReadOnlyList<Building> buildings)
    {
        return buildings.FirstOrDefault(item =>
            item.Gid == 35
            || string.Equals(item.Name, "Brewery", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BreweryCelebrationPageStatus> ReadBreweryCelebrationStatusFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _page.EvaluateAsync<JsonElement>(
            """
            () => {
              const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
              const runningTimer =
                document.querySelector('.under_progress .timer, table.under_progress .timer, .under_progress span.timer');
              const runningText = normalize(runningTimer ? runningTimer.textContent : '');
              const inProgressLabel = Array.from(document.querySelectorAll('.build_details .act .none, .under_progress, .build_details .act'))
                .map(node => normalize(node.textContent || ''))
                .find(text => /celebration is in progress/i.test(text) || /underway/i.test(text)) || '';
              const startButton = document.querySelector('.build_details td.act button');
              const canStart = !!startButton && !startButton.disabled;
              const actText = normalize(document.querySelector('.build_details td.act')?.textContent || '');
              const celebrationRunning = runningText.length > 0 || /celebration is in progress/i.test(inProgressLabel);
              const statusText = celebrationRunning
                ? 'Celebration running.'
                : canStart
                  ? 'Ready.'
                  : (actText || 'Celebration unavailable.');

              return {
                celebrationRunning,
                remainingText: runningText,
                canStart,
                statusText
              };
            }
            """);

        var celebrationRunning = payload.TryGetProperty("celebrationRunning", out var celebrationRunningNode)
            && celebrationRunningNode.ValueKind == JsonValueKind.True;
        var remainingText = payload.TryGetProperty("remainingText", out var remainingTextNode)
            ? remainingTextNode.GetString() ?? string.Empty
            : string.Empty;
        var canStart = payload.TryGetProperty("canStart", out var canStartNode)
            && canStartNode.ValueKind == JsonValueKind.True;
        var statusText = payload.TryGetProperty("statusText", out var statusTextNode)
            ? statusTextNode.GetString() ?? string.Empty
            : string.Empty;

        return new BreweryCelebrationPageStatus(
            celebrationRunning,
            remainingText,
            canStart,
            string.IsNullOrWhiteSpace(statusText) ? "Celebration unavailable." : statusText);
    }

    private async Task<BreweryCelebrationStartAttempt> TryStartBreweryCelebrationFromCurrentPageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = await _page.EvaluateAsync<bool>(
            """
            () => {
              const button = document.querySelector('.build_details td.act button');
              if (!button || button.disabled) {
                return false;
              }

              button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return true;
            }
            """);

        return started
            ? new BreweryCelebrationStartAttempt(true, "Brewery celebration started.")
            : new BreweryCelebrationStartAttempt(false, "Brewery celebration: no start button available.");
    }

    private sealed record BreweryCelebrationPageStatus(
        bool CelebrationRunning,
        string RemainingText,
        bool CanStart,
        string StatusText);

    private sealed record BreweryCelebrationStartAttempt(
        bool Started,
        string Message);
}
