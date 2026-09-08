using TbotUltra.Core.Travian;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Domain;

public sealed record Village(
    string Name,
    string? Url,
    bool? IsCapital = null,
    int? CoordX = null,
    int? CoordY = null,
    int? Population = null,
    int? CropFields = null,
    string Tribe = "Unknown");

public sealed record CapitalProfileCheckResult(string VillageName, int CoordX, int CoordY);

public sealed record AccountSnapshot(
    string Tribe,
    string ActiveVillage,
    int VillageCount,
    IReadOnlyList<Village> Villages,
    DateTimeOffset? ServerTimeUtc = null,
    int? ActiveVillageCoordX = null,
    int? ActiveVillageCoordY = null);

public sealed record ReportPngResult(bool IsReportPage, string Url, string? FilePath);

public sealed record LobbyWorldOption(string WorldUid, string Name, string Details = "")
{
    public string DisplayText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "Unnamed Travian world" : Name.Trim();
            var details = Regex.Replace(Details ?? string.Empty, @"\s+", " ").Trim();
            var uid = WorldUid.Length > 8 ? WorldUid[..8] : WorldUid;
            return details.Length > 0 && !string.Equals(details, name, StringComparison.OrdinalIgnoreCase)
                ? $"{name} · {details} · UID {uid}"
                : $"{name} · UID {uid}";
        }
    }
}

public sealed record LobbyWorldSelectionRequest(
    string ConfiguredServerName,
    string ConfiguredServerUrl,
    IReadOnlyList<LobbyWorldOption> Worlds,
    bool PreviousSelectionFailed = false);

public sealed record LobbyWorldServerResolution(
    string AccountName,
    string WorldUid,
    string WorldName,
    string ServerUrl);

public sealed record AccountAnalysisSnapshot(
    int SchemaVersion,
    DateTimeOffset AnalyzedAtUtc,
    string AccountName,
    string ServerUrl,
    string Tribe,
    bool GoldClubEnabled,
    IReadOnlyList<TribeBuildingCatalogEntry> BuildingCatalog,
    bool? AutoCelebrationEnabled = null,
    IReadOnlyList<string>? AutomationLoopEnabledGroups = null,
    IReadOnlyList<string>? AutomationLoopVisibleGroups = null,
    string? WorldUid = null,
    IReadOnlyList<Village>? Villages = null,
    bool? NewAccountAnalysisCompleted = null);

public sealed record TribeBuildingCatalogEntry(
    int Gid,
    string Name,
    string Category,
    bool IsSpecial,
    IReadOnlyList<BuildingRequirementEntry> Requirements);

public sealed record TribeBuildingCatalogFullEntry(
    int Gid,
    string Name,
    string Category,
    bool IsSpecial,
    string? RequiredTribe,
    bool MatchesPlayerTribe,
    IReadOnlyList<BuildingRequirementEntry> Requirements);

public sealed record BuildingRequirementEntry(string Name, int Level);

public sealed record ResourceField(
    int? SlotId,
    string FieldType,
    string Name,
    int? Level,
    string? Url,
    int? QueuedLevel = null);

public sealed record Building(
    int? SlotId,
    string Name,
    int? Level,
    string? Url,
    int? Gid = null);

public sealed record BuildQueueItem(
    string Text,
    string? TimeLeft,
    int? SlotId = null,
    int? Gid = null,
    string? Href = null);

public enum ConstructionKind { Resource, Building, Unknown }

public enum ActiveConstructionReadMode
{
    FreshForMutation,
    CachedForObservation,
}

public sealed record TimerSnapshot(
    int RemainingSeconds,
    DateTimeOffset ReadAtUtc,
    DateTimeOffset FinishUtc,
    bool FromServerTime)
{
    public static TimerSnapshot FromRemaining(int remainingSeconds, DateTimeOffset? serverTimeUtc = null)
    {
        var normalizedSeconds = Math.Max(0, remainingSeconds);
        var readAtUtc = serverTimeUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        return new TimerSnapshot(
            normalizedSeconds,
            readAtUtc,
            readAtUtc.AddSeconds(normalizedSeconds),
            serverTimeUtc.HasValue);
    }

    public int RemainingSecondsAt(DateTimeOffset now)
    {
        return Math.Max(0, (int)Math.Ceiling((FinishUtc - now.ToUniversalTime()).TotalSeconds));
    }

    public bool IsFinishedAt(DateTimeOffset now)
    {
        return FinishUtc <= now.ToUniversalTime();
    }
}

public sealed record ActiveConstruction(
    ConstructionKind Kind,
    string Name,
    int? Level,
    int? TimeLeftSeconds,
    string? FinishAtText,
    TimerSnapshot? Finish = null,
    int? SlotId = null,
    int? Gid = null,
    string? Href = null);

public sealed record ConstructionSlotStatus(
    IReadOnlyList<ActiveConstruction> Active,
    int ResourceSlotsUsed,
    int BuildingSlotsUsed,
    int ResourceSlotsMax,
    int BuildingSlotsMax,
    bool CanStartResource,
    bool CanStartBuilding,
    int? ShortestWaitSeconds);

public sealed record ResourceStorageForecast(
    string ResourceKey,
    long? Current,
    long? Capacity,
    double? PercentOfCapacity,
    double? ProductionPerHour,
    int? SecondsToFull,
    int? SecondsToEmpty = null);

public sealed record HeroStatus(
    bool Exists = false,
    bool IsDead = false,
    string State = "Unknown",
    int? HpPercent = null,
    int AdventuresAvailable = 0,
    int? SecondsUntilAdventureReady = null,
    int? SecondsUntilReturn = null,
    int? ReviveRemainingSeconds = null,
    int UnassignedPoints = 0,
    string? MovementState = null,
    TimerSnapshot? AdventureReadyFinish = null,
    TimerSnapshot? ReturnFinish = null,
    TimerSnapshot? ReviveFinish = null);

public sealed record HeroRuntimeStatus(
    string DisplayText,
    bool IsAway,
    bool IsDead,
    bool IsReviving);

public sealed record HeroAttributeSnapshot(
    bool LevelUpAvailable = false,
    int FreePoints = 0,
    int FightingStrength = 0,
    int OffenceBonus = 0,
    int DefenceBonus = 0,
    int Resources = 0,
    int? AdventureCount = null,
    string HeroState = "Unknown",
    int? ReviveRemainingSeconds = null,
    // The hero's home village name, read from the attributes page ("Home village is village X").
    // Null when not found. Used by the dashboard to mark which village hosts the hero.
    string? HomeVillageName = null,
    // Whether the hero is currently away from the home village (on an adventure/attack/etc) rather than
    // standing in it. Drives the green (home) vs yellow (away) hero icon on the dashboard.
    bool HomeVillageHeroAway = false,
    int? HomeVillageCoordX = null,
    int? HomeVillageCoordY = null,
    // Live movement state and remaining return time read from the same Attributes page. These values let
    // the login flow project the Hero countdown before automation starts instead of revisiting the page.
    string? MovementState = null,
    int? SecondsUntilReturn = null);

public sealed record HeroInventoryResources(
    int Wood = 0,
    int Clay = 0,
    int Iron = 0,
    int Crop = 0);

public enum HeroInventoryObservationSource
{
    Unknown = 0,
    HeroInventoryPage = 1,
    TransferDialog = 2,
    EmptyToast = 3,
    TransferDeduction = 4,
}

public sealed record HeroInventorySnapshot(
    HeroInventoryResources Resources,
    DateTimeOffset UpdatedAtUtc,
    HeroInventoryObservationSource Source = HeroInventoryObservationSource.Unknown,
    int ConsecutiveEmptyObservations = 0,
    DateTimeOffset? NextProbeAtUtc = null);

public sealed record HeroAdventureDispatchResult(
    bool IsInHomeVillage,
    string? StatusText,
    int AdventureCount,
    bool Dispatched,
    int? SecondsUntilReturn,
    string Message,
    bool WasRevived = false,
    bool IsOnTheWayHome = false);

public sealed record TroopTrainingQueueStatus(
    TroopTrainingBuildingType BuildingType,
    string BuildingName,
    bool Exists,
    int? SlotId,
    IReadOnlyList<BuildQueueItem> QueueItems,
    int? RemainingSeconds,
    string RemainingText,
    TimerSnapshot? Finish = null);

public sealed record BreweryCelebrationStatus(
    bool IsAvailableForTribe,
    bool? IsCapital,
    bool BreweryExists,
    int? BrewerySlotId,
    bool CelebrationRunning,
    int? RemainingSeconds,
    string RemainingText,
    string StatusText,
    TimerSnapshot? Finish = null);

public sealed record ActiveSmithyUpgrade(
    string Name,
    int? TargetLevel,
    int? TimeLeftSeconds,
    TimerSnapshot? Finish = null);

public sealed record SmithyUpgradeStatus(
    bool SmithyExists,
    int? SmithySlotId,
    int ActiveUpgradeCount,
    int? RemainingSeconds,
    IReadOnlyList<int> ActiveUpgradeRemainingSeconds,
    string RemainingText,
    string StatusText,
    IReadOnlyList<TimerSnapshot>? ActiveUpgradeFinishes = null,
    IReadOnlyList<ActiveSmithyUpgrade>? ActiveUpgrades = null);

public sealed record VillageStatus(
    string ActiveVillage,
    IReadOnlyList<Village> Villages,
    IReadOnlyDictionary<string, string> Resources,
    IReadOnlyList<ResourceField> ResourceFields,
    IReadOnlyList<Building> Buildings,
    IReadOnlyList<BuildQueueItem> BuildQueue,
    string Tribe = "Unknown",
    int VillageCount = 0,
    int? Gold = null,
    int? Silver = null,
    bool IsBuildingInProgress = false,
    int ActiveBuildCount = 0,
    int? BuildQueueRemainingSeconds = null,
    string BuildQueueRemainingText = "",
    bool? IsCapital = null,
    DateTimeOffset? ServerTimeUtc = null,
    int UnreadMessages = 0,
    int UnreadReports = 0,
    long? WarehouseCapacity = null,
    long? GranaryCapacity = null,
    IReadOnlyList<ResourceStorageForecast>? ResourceStorageForecasts = null,
    IReadOnlyList<TroopTrainingQueueStatus>? TroopTrainingQueues = null,
    int? AdventureCount = null,
    IReadOnlyList<ActiveConstruction>? ActiveConstructions = null,
    TimerSnapshot? BuildQueueFinish = null,
    SmithyUpgradeStatus? SmithyUpgradeStatus = null,
    BreweryCelebrationStatus? BreweryCelebrationStatus = null,
    IReadOnlyList<FarmListOverview>? FarmLists = null,
    HeroStatus? HeroStatus = null,
    bool ActiveConstructionsFromOverview = false,
    // The active village's own coordinates, read from the sidebar (#villageName data-x/data-y). The
    // exact identity of the village this status belongs to — unlike ActiveVillage (a display name),
    // coordinates are unique and survive renames, so caches can key on them without name matching.
    int? ActiveVillageCoordX = null,
    int? ActiveVillageCoordY = null,
    // Null means neither Dorf1 nor a Plus village overview was inspected. An empty list means the
    // available signal source was inspected and no incoming-attack signal was visible.
    IReadOnlyList<IncomingAttackSignal>? IncomingAttackSignals = null,
    // Plus village-list signals can be read from any game page. Only a real Dorf1 read may use a
    // missing active-village signal as authoritative evidence that the active village is clear.
    bool IncomingAttackActiveVillageReadWasAuthoritative = false,
    // True only when the complete account-wide Plus village list was available in this page read.
    bool IncomingAttackPlusOverviewWasRead = false,
    // Null means the Dorf1 unit infobox was not inspected. False is authoritative only for that live
    // Dorf1 read and means Travian rendered #troops td.noTroops (no units currently at home).
    bool? HasTroopsAtHome = null,
    DateTimeOffset? TroopPresenceObservedAtUtc = null,
    // Current-page observation captured by the jitter resource read. Null means the page did not
    // expose an authoritative Plus marker, so consumers must retain their last known state.
    bool? TravianPlusActive = null);

public enum IncomingAttackMovementType
{
    Unknown,
    Attack,
    Raid,
}

public sealed record IncomingAttackSignal(
    string VillageName,
    string? VillageUrl = null,
    int? VillageId = null,
    int? CoordX = null,
    int? CoordY = null,
    DateTimeOffset? ObservedAtUtc = null,
    IReadOnlyList<DateTimeOffset>? Dorf1ArrivalTimesUtc = null);

public sealed record IncomingAttack(
    string Id,
    string TargetVillageName,
    DateTimeOffset ArrivalAtUtc,
    IncomingAttackMovementType MovementType = IncomingAttackMovementType.Unknown,
    string? TargetVillageKey = null,
    int? TargetCoordX = null,
    int? TargetCoordY = null,
    string? SourcePlayerName = null,
    string? SourceVillageName = null,
    int? SourceCoordX = null,
    int? SourceCoordY = null,
    DateTimeOffset? ObservedAtUtc = null);

public sealed record IncomingAttackSnapshot(
    string TargetVillageName,
    string? TargetVillageKey,
    int? TargetCoordX,
    int? TargetCoordY,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<IncomingAttack> Attacks,
    bool RallyPointReadSucceeded = true,
    IReadOnlyList<DateTimeOffset>? Dorf1FallbackArrivalTimesUtc = null);

public sealed record InboxStatus(
    int UnreadMessages = 0,
    int UnreadReports = 0);

public sealed record PostLoginSnapshot(
    VillageStatus VillageStatus,
    InboxStatus InboxStatus,
    int? AdventureCount,
    HeroInventoryResources? HeroInventory = null,
    bool NewAccountAnalysisPending = false);

public sealed record PageHtmlCapture(string Url, string Html);

public sealed record FarmListOverview(
    string Name,
    int ActiveFarmCount,
    int TotalFarmCount,
    int? RemainingSeconds,
    string? ListId = null,
    int? Capacity = null,
    IReadOnlyList<string>? FarmCoordinates = null,
    TimerSnapshot? Finish = null,
    bool TimerIsEstimated = false,
    // Display name of the village that owns this farm list (from the .villageWrapper header). Used only
    // to group lists under a village heading in the UI; it is re-read on every analyze, so a village
    // rename simply re-groups on the next read and never breaks the stable list identity (ListId).
    string? VillageName = null,
    // Ordinal of the owning .villageWrapper on the page. The page exposes no village id/coordinates on the
    // wrapper, so this ordinal is the grouping key that keeps two villages with the same display name apart.
    int? VillageIndex = null);

public sealed record FarmListLossDeactivationResult(
    int RowsFound,
    int RowsDeactivated,
    int SkippedOasisRows,
    int RowsMoved = 0,
    int MoveFailures = 0,
    string? DestinationListId = null,
    string? DestinationListName = null,
    bool DestinationChanged = false,
    FarmListLossColors LossColors = FarmListLossColors.Both);

[Flags]
public enum FarmListLossColors
{
    None = 0,
    Red = 1,
    Yellow = 2,
    Both = Red | Yellow,
}

public sealed record FarmListLossHandlingRequest(
    bool IncludeUnoccupiedOasis,
    bool MoveLosses,
    string DestinationListId,
    string DestinationListName,
    string DestinationBaseName,
    FarmListCreateRequest? CreateTemplate = null,
    int? MaxTargets = null,
    FarmListLossColors LossColors = FarmListLossColors.Both,
    bool IncludeNonOasisLosses = true);

public sealed record FarmLossDestinationChange(
    string AccountName,
    string ListId,
    string ListName,
    string BaseName,
    string VillageName,
    FarmListLossColors LossColors = FarmListLossColors.Both);

public sealed record FarmCoordinate(int X, int Y, bool RequireUnoccupiedOasis = false);

public sealed record FarmAddProgress(
    string FarmListName,
    int ProcessedCount,
    int TotalCount,
    int AddedCount,
    int NotFoundCount,
    // Carries the coordinate of a target that had no village, so a cancelled run can still offer to
    // remove the dead coordinates found so far. Null for every other progress report.
    FarmCoordinate? InvalidCoordinate = null,
    int OccupiedOasisSkippedCount = 0);

public sealed record FarmAddResult(
    string FarmListName,
    int X,
    int Y,
    string TroopType,
    int TroopCount);

public sealed record FarmAddBatchResult(
    string FarmListName,
    int RequestedCount,
    int AttemptedCount,
    int AddedCount,
    int AlreadyInListCount,
    int FailedCount,
    int NotFoundCount = 0,
    IReadOnlyList<FarmCoordinate>? InvalidCoordinates = null,
    int OccupiedOasisSkippedCount = 0);

public sealed record FarmListCreateRequest(
    IReadOnlyList<string> Names,
    string VillageName,
    string? VillageId,
    string TroopType,
    int TroopCount,
    int? TroopIndexOverride = null,
    bool OnlyCreateReportsWithLosses = true);

public sealed record FarmListCreateProgress(
    string Phase,
    int ProcessedCount,
    int TotalCount,
    string? FarmListName = null);

public sealed record FarmListCreateBatchResult(
    int RequestedCount,
    int CreatedCount,
    IReadOnlyList<string> CreatedNames);

public sealed record CatapultWaveRequest(
    int X,
    int Y,
    int WaveCount,
    bool RaidAttack,
    IReadOnlyDictionary<string, int> FirstAttackTroops,
    IReadOnlyDictionary<string, int> WaveTroops,
    string? Target1,
    string? Target2,
    int TabOpenDelayMilliseconds = 100);

public sealed record CatapultWaveSetupInfo(
    IReadOnlyDictionary<string, long> AvailableTroops,
    int? RallyPointLevel);

public sealed record CatapultWaveRunResult(
    int TotalAttacks,
    int PreparedCount,
    int SentCount,
    int FailedCount,
    int X,
    int Y);
