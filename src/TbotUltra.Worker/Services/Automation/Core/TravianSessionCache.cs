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
    public string? SessionTribe { get; set; }
    public System.DateTimeOffset CachedTribePlusAt { get; set; } = System.DateTimeOffset.MinValue;

    public System.DateTimeOffset LastEnsureLoggedInAt { get; set; } = System.DateTimeOffset.MinValue;
    public bool LastEnsureLoggedInSucceeded { get; set; }

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
    public string? TroopQueueSnapshotVillage { get; set; }
    public System.Collections.Generic.Dictionary<TbotUltra.Core.Travian.TroopTrainingBuildingType, Domain.TroopTrainingQueueStatus>? TroopQueueSnapshotByBuilding { get; set; }
    public System.DateTimeOffset TroopQueueSnapshotAt { get; set; } = System.DateTimeOffset.MinValue;
}
