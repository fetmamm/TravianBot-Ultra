using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;

namespace TbotUltra.Worker.Services;

/// <summary>Account/world-scoped retry gate after a completed scan found no safe target.</summary>
public sealed class HeroRaidingScheduleStore(string projectRoot, Action<string> log)
{
    private static readonly TimeSpan NoCandidateRetry = TimeSpan.FromMinutes(60);

    public DateTimeOffset? LoadNextDiscoveryUtc(string accountName, string? serverUrl)
    {
        var path = AccountStoragePaths.HeroRaidingSchedulePath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ScheduleState>(File.ReadAllText(path));
            if (state is null || !string.Equals(state.ServerUrl, serverUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hero raiding schedule has the wrong world or an invalid document.");
            return state.NextDiscoveryUtc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A lost retry gate could cause repeated map API calls. Fail closed until repaired.
            throw new InvalidOperationException("Hero raiding cannot read its saved discovery schedule.", ex);
        }
    }

    public bool IsDiscoveryDue(string accountName, string? serverUrl, DateTimeOffset nowUtc)
    {
        var next = LoadNextDiscoveryUtc(accountName, serverUrl);
        return next is null || next <= nowUtc;
    }

    public DateTimeOffset RecordNoCandidate(string accountName, string? serverUrl, DateTimeOffset nowUtc)
    {
        var next = nowUtc.ToUniversalTime() + NoCandidateRetry;
        Save(accountName, serverUrl, next);
        log($"[hero-raiding] no safe target; next discovery at {next:O} (60-minute quiet period).");
        return next;
    }

    public void ClearAfterSuccessfulScan(string accountName, string? serverUrl)
    {
        Save(accountName, serverUrl, null);
        log("[hero-raiding] safe-target scan completed; the no-candidate retry gate was cleared.");
    }

    private void Save(string accountName, string? serverUrl, DateTimeOffset? nextDiscoveryUtc)
    {
        var path = AccountStoragePaths.HeroRaidingSchedulePath(projectRoot, accountName, serverUrl);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new ScheduleState(serverUrl ?? string.Empty, nextDiscoveryUtc)));
    }

    private sealed record ScheduleState(string ServerUrl, DateTimeOffset? NextDiscoveryUtc);
}
