using System.Text.Json;
using System.IO;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services;

public sealed record IncomingAttackPersistedState(
    IReadOnlyList<IncomingAttack> Attacks,
    IReadOnlyList<IncomingAttackSignal> PendingSignals,
    IReadOnlyDictionary<string, int> ConfirmedMovementCounts);

public sealed class IncomingAttackStore(string projectRoot, Action<string>? log = null)
{
    private sealed record IncomingAttackFile(
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<IncomingAttack> Attacks,
        IReadOnlyList<IncomingAttackSignal> PendingSignals,
        IReadOnlyDictionary<string, int>? ConfirmedMovementCounts = null);

    private const int CurrentSchemaVersion = 2;
    private static readonly object FileIoLock = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public IncomingAttackPersistedState Load(
        string? accountName,
        string? serverUrl,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return new IncomingAttackPersistedState([], [], new Dictionary<string, int>());
        }

        var path = AccountStoragePaths.IncomingAttacksSnapshotPath(projectRoot, accountName, serverUrl);
        if (!File.Exists(path))
        {
            return new IncomingAttackPersistedState([], [], new Dictionary<string, int>());
        }

        try
        {
            IncomingAttackFile? file;
            lock (FileIoLock)
            {
                file = JsonSerializer.Deserialize<IncomingAttackFile>(File.ReadAllText(path), SerializerOptions);
            }

            if (file is null || file.SchemaVersion is not (1 or CurrentSchemaVersion))
            {
                return new IncomingAttackPersistedState([], [], new Dictionary<string, int>());
            }

            var activeAttacks = (file.Attacks ?? [])
                .Where(attack => attack.ArrivalAtUtc > nowUtc)
                .ToList();
            var confirmedMovementCounts = file.ConfirmedMovementCounts is { Count: > 0 }
                ? new Dictionary<string, int>(file.ConfirmedMovementCounts, StringComparer.OrdinalIgnoreCase)
                : activeAttacks
                    .GroupBy(attack => attack.TargetVillageKey ?? attack.TargetVillageName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var activeAttackKeys = activeAttacks
                .Select(attack => attack.TargetVillageKey ?? attack.TargetVillageName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pendingSignals = (file.PendingSignals ?? [])
                .Where(signal =>
                {
                    var key = signal.CoordX.HasValue && signal.CoordY.HasValue
                        ? $"xy:{signal.CoordX.Value}|{signal.CoordY.Value}"
                        : signal.VillageName;
                    return IncomingAttackObservationPolicy.ShouldKeepPendingSignal(
                        signal,
                        activeAttackKeys.Contains(key),
                        confirmedMovementCounts.ContainsKey(key),
                        nowUtc);
                })
                .ToList();
            var removedPendingCount = (file.PendingSignals?.Count ?? 0) - pendingSignals.Count;
            if (removedPendingCount > 0)
            {
                log?.Invoke($"[incoming-attacks] discarded {removedPendingCount} expired pending warning(s) from snapshot.");
            }
            return new IncomingAttackPersistedState(
                activeAttacks,
                pendingSignals,
                confirmedMovementCounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"[incoming-attacks] could not load snapshot: {ex.Message}");
            return new IncomingAttackPersistedState([], [], new Dictionary<string, int>());
        }
    }

    public void Save(
        string? accountName,
        string? serverUrl,
        IReadOnlyCollection<IncomingAttack> attacks,
        IReadOnlyCollection<IncomingAttackSignal> pendingSignals,
        IReadOnlyDictionary<string, int>? confirmedMovementCounts = null)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        var path = AccountStoragePaths.IncomingAttacksSnapshotPath(projectRoot, accountName, serverUrl);
        try
        {
            var file = new IncomingAttackFile(
                CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                attacks.OrderBy(attack => attack.ArrivalAtUtc).ToList(),
                pendingSignals.ToList(),
                confirmedMovementCounts is null
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int>(confirmedMovementCounts, StringComparer.OrdinalIgnoreCase));
            lock (FileIoLock)
            {
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"[incoming-attacks] could not save snapshot '{path}': {ex.Message}");
        }
    }
}
