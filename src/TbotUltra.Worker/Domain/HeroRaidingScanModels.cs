namespace TbotUltra.Worker.Domain;

public sealed record HeroRaidingScanCandidate(
    int X,
    int Y,
    string OasisType,
    string Animals,
    int? PredictedHpAfterRaid,
    int? EstimatedBountyPerResource,
    double DistanceFields,
    bool IsExcluded,
    bool IsSafe,
    DateTimeOffset ObservedAtUtc);

public sealed record HeroRaidingScanResult(
    bool HeroReady,
    bool CagesEquipped,
    IReadOnlyList<HeroRaidingScanCandidate> Candidates,
    int AreasRead,
    int CachedAreasUsed,
    string Message);
