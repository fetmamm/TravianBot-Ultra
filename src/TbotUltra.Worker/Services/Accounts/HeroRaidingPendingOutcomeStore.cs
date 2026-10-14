using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed record HeroRaidingPendingOutcome(
    int SourceX,
    int SourceY,
    int TargetX,
    int TargetY,
    int StartingHpPercent,
    int PredictedHpAfterRaid,
    int MinimumHpPercent,
    DateTimeOffset SentAtUtc,
    DateTimeOffset? ExpectedHomeUtc = null,
    bool OutgoingVerified = false,
    bool SafetyDisabled = false);

/// <summary>
/// Keeps one unverified raid across restart. A corrupt record fails closed: the worker
/// must not send another hero raid until the previous outcome is resolved.
/// </summary>
public sealed class HeroRaidingPendingOutcomeStore(string projectRoot, Action<string> log)
{
    public HeroRaidingPendingOutcome? Load(string accountName, string? serverUrl)
    {
        var path = AccountStoragePaths.HeroRaidingPendingOutcomePath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<PendingState>(File.ReadAllText(path));
            if (state is null || !string.Equals(state.ServerUrl, serverUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hero raiding pending outcome belongs to another world.");
            if (state.Pending is { } pending && !IsValid(pending))
                throw new InvalidDataException("Hero raiding pending outcome contains invalid raid data.");
            return state.Pending;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidOperationException("Hero raiding cannot verify its saved pending outcome; new raids are blocked.", ex);
        }
    }

    public void SaveSent(string accountName, string? serverUrl, HeroRaidingPendingOutcome pending)
    {
        if (!IsValid(pending))
            throw new ArgumentOutOfRangeException(nameof(pending), "Hero raiding pending outcome has invalid raid data.");
        var path = AccountStoragePaths.HeroRaidingPendingOutcomePath(projectRoot, accountName, serverUrl);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new PendingState(serverUrl ?? string.Empty, pending)));
        log($"[hero-raiding] saved pending raid from ({pending.SourceX}|{pending.SourceY}) to ({pending.TargetX}|{pending.TargetY}) at {pending.SentAtUtc:O}.");
    }

    public void ClearVerified(string accountName, string? serverUrl)
    {
        var path = AccountStoragePaths.HeroRaidingPendingOutcomePath(projectRoot, accountName, serverUrl);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new PendingState(serverUrl ?? string.Empty, null)));
        log("[hero-raiding] verified raid outcome cleared; next raid may be considered.");
    }

    public void MarkSafetyDisabled(string accountName, string? serverUrl, HeroRaidingPendingOutcome pending)
    {
        SaveSent(accountName, serverUrl, pending with { SafetyDisabled = true });
        log("[hero-raiding] pending raid was safety-disabled; user must explicitly re-enable raiding before another send.");
    }

    private static bool IsValid(HeroRaidingPendingOutcome pending) =>
        pending.SourceX is >= -200 and <= 200
        && pending.SourceY is >= -200 and <= 200
        && pending.TargetX is >= -200 and <= 200
        && pending.TargetY is >= -200 and <= 200
        && (pending.SourceX != pending.TargetX || pending.SourceY != pending.TargetY)
        && pending.StartingHpPercent is >= 1 and <= 100
        && pending.PredictedHpAfterRaid is >= 1 and <= 100
        && pending.MinimumHpPercent is >= 1 and <= 100
        && pending.SentAtUtc != default
        && (pending.ExpectedHomeUtc is null
            || (pending.ExpectedHomeUtc > pending.SentAtUtc
                && pending.ExpectedHomeUtc < pending.SentAtUtc.AddDays(7)))
        && (!pending.OutgoingVerified || pending.ExpectedHomeUtc is not null);

    private sealed record PendingState(string ServerUrl, HeroRaidingPendingOutcome? Pending);
}
