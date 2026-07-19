namespace TbotUltra.Worker.Services;

/// <summary>
/// Holds session-scoped cache state that must survive across the short-lived <see cref="TravianClient"/>
/// instances created per operation for the same shared browser session/account. Without this, the
/// per-instance caches reset on every operation and cheap-but-repeated probes (Travian Plus / Gold
/// Club / tribe and the logged-in check) run again and again during the post-login burst.
/// </summary>
public sealed class TravianSessionCache
{
    public bool? CachedTravianPlusActive { get; set; }
    public bool? CachedGoldClubEnabled { get; set; }
    public int? CachedGold { get; set; }
    public int? CachedSilver { get; set; }
    public System.DateTimeOffset CachedCurrencyAt { get; set; } = System.DateTimeOffset.MinValue;
    public System.TimeSpan? CachedServerUtcOffset { get; set; }
    /// <summary>The avatar/account tribe selected at registration.</summary>
    public string? AccountTribe { get; set; }

    /// <summary>Known village tribes keyed by stable did/coordinates (name is session-only fallback).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> VillageTribes { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);
    public System.DateTimeOffset CachedTribePlusAt { get; set; } = System.DateTimeOffset.MinValue;

    public System.DateTimeOffset LastEnsureLoggedInAt { get; set; } = System.DateTimeOffset.MinValue;
    public bool LastEnsureLoggedInSucceeded { get; set; }
    public int ConsecutiveUnknownAccessStates { get; set; }
    public System.DateTimeOffset LastResourceReadLogAt { get; set; } = System.DateTimeOffset.MinValue;

    // Next time hero_manage may dispatch an adventure. Set when an adventure is first observed while
    // the hero is home, so repeated worker attempts reuse one deadline instead of drawing a new random
    // delay forever. No deadline is created while the hero is away or no adventure exists.
    public System.DateTimeOffset? HeroAdventureDispatchNotBeforeUtc { get; set; }

    // Short-lived construction snapshot shared by the per-operation TravianClient instances that
    // use the same visible browser. Navigation and construction mutations invalidate it centrally.
    public System.Collections.Generic.IReadOnlyList<Domain.ActiveConstruction>? CachedActiveConstructions { get; set; }
    public System.DateTimeOffset CachedActiveConstructionsAt { get; set; } = System.DateTimeOffset.MinValue;
    public bool CachedActiveConstructionsFromOverview { get; set; }
    public string? CachedActiveConstructionsVillageKey { get; set; }

    // Villages list + population cache. Shared so the bylist/population read from spieler.php once
    // survives across per-operation clients (no duplicate spieler navigation at startup) and the
    // population baseline persists between operations.
    public System.Collections.Generic.List<Domain.Village>? CachedVillages { get; set; }
    public System.DateTimeOffset CachedVillagesAt { get; set; } = System.DateTimeOffset.MinValue;
    public System.DateTimeOffset CachedVillagesPopulationAt { get; set; } = System.DateTimeOffset.MinValue;
    public bool PopulationBaselineRead { get; set; }

    // Troop training queue statuses read while build_troops ran, keyed by building type, plus the
    // village they were read on. The post-build queue read reuses them (when fresh and still on the
    // same village) instead of re-navigating to each troop building the task just visited.
    public string? TroopQueueSnapshotVillageKey { get; set; }
    public System.Collections.Generic.Dictionary<TbotUltra.Core.Travian.TroopTrainingBuildingType, Domain.TroopTrainingQueueStatus>? TroopQueueSnapshotByBuilding { get; set; }
    public System.DateTimeOffset TroopQueueSnapshotAt { get; set; } = System.DateTimeOffset.MinValue;

    // Ongoing-construction count seen at the previous construction start-gate check, keyed by
    // "{villageNewdid}:{slotCategory}". Lets the humanized start delay tell "a build just finished,
    // slot freed" (previous>0, current==0) from a genuinely idle start. Survives the per-operation
    // TravianClient instances so the deferred attempts and the eventual start share the same memory.
    public System.Collections.Generic.Dictionary<string, int> ConstructionOngoingByKey { get; } = new();

    // When the humanized construction delay defers a specific build, the computed "start no earlier
    // than" deadline is stored here keyed by "{villageNewdid}:{kind}:{slotId}". On the retry after
    // the wait the gate finds now>=deadline and lets the build proceed instead of re-computing (which
    // would defer forever). Survives per-operation TravianClient instances like the other caches.
    public System.Collections.Generic.Dictionary<string, System.DateTimeOffset> ConstructionHumanizeUntilBySlot { get; } = new();

    /// <summary>The persisted humanize generation currently represented by the two caches above.</summary>
    public int? ConstructionHumanizeStateVersion { get; private set; }

    /// <summary>Clears construction pacing memory when the user toggles humanization off or on.</summary>
    public bool SynchronizeConstructionHumanizeState(int stateVersion)
    {
        if (ConstructionHumanizeStateVersion == stateVersion)
        {
            return false;
        }

        ConstructionOngoingByKey.Clear();
        ConstructionHumanizeUntilBySlot.Clear();
        ConstructionHumanizeStateVersion = stateVersion;
        return true;
    }

    // Last value logged per key, so repeated per-tick status echoes (population, adventure counts,
    // language, …) are only re-logged when the value actually changes instead of every refresh.
    private readonly System.Collections.Generic.Dictionary<string, string> _lastLoggedValueByKey = new();

    /// <summary>
    /// Returns true (and stores the value) when <paramref name="value"/> differs from the last value
    /// logged for <paramref name="key"/>; false when unchanged. Gates high-frequency status logs so
    /// they print once and then only again on change. Logging-only — never gate behavior on this.
    /// </summary>
    public bool LogValueChanged(string key, string value)
    {
        if (_lastLoggedValueByKey.TryGetValue(key, out var last) && last == value)
        {
            return false;
        }

        _lastLoggedValueByKey[key] = value;
        return true;
    }
}
