using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

internal sealed record HeroRaidingDiscoveryInput(
    int HeroVillageX,
    int HeroVillageY,
    int MaximumDistance,
    int ObservationMinutes,
    IReadOnlyCollection<string> SelectedTypes,
    IReadOnlySet<(int X, int Y)> Excluded,
    int HeroAttackStrength,
    int CurrentHpPercent,
    bool HeroMounted,
    int MinimumHpPercent,
    bool ScanAllAreas = false);

internal sealed record HeroRaidingSafeCandidate(
    HeroRaidingCandidate Candidate,
    HeroOasisRaidEstimate Estimate);

internal sealed record HeroRaidingDiscoveryResult(
    IReadOnlyList<HeroRaidingSafeCandidate> SafeCandidates,
    IReadOnlyList<MapOasisEntry> ObservedOases,
    int AreasRead,
    int CachedAreasUsed);

/// <summary>
/// Discovers local targets one map area at a time. A fresh cached area is reused, and
/// expansion stops as soon as its observed animals yield a safe candidate.
/// </summary>
internal sealed class HeroRaidingDiscoveryOperation(
    IMapOasisAreaReader reader,
    string projectRoot,
    string accountName,
    string serverUrl,
    Action<string> log)
{
    internal async Task<HeroRaidingDiscoveryResult> ExecuteAsync(
        HeroRaidingDiscoveryInput input,
        CancellationToken cancellationToken)
    {
        if (input.HeroVillageX is < -200 or > 200 || input.HeroVillageY is < -200 or > 200
            || input.MaximumDistance is < 1 or > 50 || input.ObservationMinutes is < 1 or > 120
            || input.HeroAttackStrength <= 0 || input.CurrentHpPercent is < 1 or > 100
            || input.MinimumHpPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(input), "Hero raiding discovery received invalid hero or map settings.");

        var now = DateTimeOffset.UtcNow;
        var path = AccountStoragePaths.HeroRaidingAreaCachePath(projectRoot, accountName, serverUrl);
        var areas = LoadAreas(path, now);
        var zoom = areas.Count > 0 ? areas[^1].ZoomLevel : 3;
        var readCount = 0;
        var cachedCount = 0;
        var seen = new Dictionary<(int X, int Y), MapOasisEntry>();
        List<HeroRaidingSafeCandidate> safe = [];

        while (true)
        {
            var (horizontalRadius, verticalRadius) = zoom switch
            {
                3 => (15, 15),
                2 => (10, 8),
                _ => (5, 4),
            };
            var centers = MapOasisApiParser.CreateScanCenters(
                Math.Max(-200, input.HeroVillageX - input.MaximumDistance),
                Math.Min(200, input.HeroVillageX + input.MaximumDistance),
                Math.Max(-200, input.HeroVillageY - input.MaximumDistance),
                Math.Min(200, input.HeroVillageY + input.MaximumDistance),
                horizontalRadius, verticalRadius,
                input.HeroVillageX, input.HeroVillageY);

            var retryAtLowerZoom = false;
            foreach (var center in centers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cached = areas.FirstOrDefault(area => area.ZoomLevel == zoom
                    && area.CenterX == center.X && area.CenterY == center.Y
                    && area.ObservedAtUtc <= now
                    && now - area.ObservedAtUtc <= TimeSpan.FromMinutes(input.ObservationMinutes));
                IReadOnlyList<MapOasisEntry> observed;
                if (cached is not null)
                {
                    cachedCount++;
                    observed = cached.Oases;
                }
                else
                {
                    if (readCount > 0)
                    {
                        var delay = TimeSpan.FromSeconds(1.5 + Random.Shared.NextDouble() * 2);
                        await Task.Delay(delay, cancellationToken);
                    }

                    var json = await ReadWithRetryAsync(center.X, center.Y, zoom, cancellationToken);
                    readCount++;
                    if (MapOasisApiParser.IsRegionOverlay(json))
                    {
                        if (zoom == 1)
                            throw new InvalidOperationException("Hero raiding map scan returned only regional overlays at detailed zoom level 1.");
                        log($"[hero-raiding] regional overlay at zoom {zoom}; retrying at zoom {zoom - 1}.");
                        zoom--;
                        retryAtLowerZoom = true;
                        break;
                    }

                    var observedAt = DateTimeOffset.UtcNow;
                    observed = MapOasisApiParser.Parse(json)
                        .Select(oasis => oasis with { ObservedAtUtc = observedAt })
                        .ToList();
                    areas.RemoveAll(area => area.ZoomLevel == zoom
                        && area.CenterX == center.X && area.CenterY == center.Y);
                    areas.Add(new CachedArea(center.X, center.Y, zoom, observedAt, observed.ToList()));
                    areas = areas.OrderBy(area => area.ObservedAtUtc).TakeLast(128).ToList();
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new AreaCache(serverUrl, areas)));
                    log($"[hero-raiding] read map area ({center.X}|{center.Y}) zoom={zoom}; oases={observed.Count}.");
                }

                foreach (var oasis in observed)
                {
                    var key = (oasis.X, oasis.Y);
                    if (!seen.TryGetValue(key, out var previous)
                        || oasis.ObservedAtUtc >= previous.ObservedAtUtc)
                        seen[key] = oasis;
                }

                var selected = HeroRaidingCandidateSelector.Select(
                    seen.Values, input.HeroVillageX, input.HeroVillageY,
                    input.MaximumDistance, input.ObservationMinutes, input.SelectedTypes,
                    input.Excluded, DateTimeOffset.UtcNow);
                safe = selected.Select(candidate =>
                {
                    var estimated = HeroOasisRaidEstimator.TryEstimate(
                        candidate.Garrison, input.HeroAttackStrength, input.CurrentHpPercent,
                        input.HeroMounted, out var estimate);
                    return estimated && estimate!.PredictedHpAfterRaid >= input.MinimumHpPercent
                        ? new HeroRaidingSafeCandidate(candidate, estimate)
                        : null;
                }).Where(candidate => candidate is not null).Cast<HeroRaidingSafeCandidate>()
                    .OrderByDescending(candidate => candidate.Estimate.EstimatedBountyPerResource / candidate.Candidate.DistanceFields)
                    .ThenBy(candidate => candidate.Estimate.PredictedHpLoss)
                    .ThenBy(candidate => candidate.Candidate.DistanceFields)
                    .ToList();
                if (safe.Count > 0 && !input.ScanAllAreas)
                {
                    log($"[hero-raiding] found {safe.Count} safe oasis candidate(s) after {readCount} map read(s), {cachedCount} cached area(s).");
                    return new HeroRaidingDiscoveryResult(safe, seen.Values.ToList(), readCount, cachedCount);
                }
            }

            if (retryAtLowerZoom)
            {
                seen.Clear();
                safe = [];
                continue;
            }

            log(input.ScanAllAreas
                ? $"[hero-raiding] full scan completed: {safe.Count} safe oasis candidate(s), {seen.Count} observed oasis(es), {readCount} map read(s), {cachedCount} cached area(s)."
                : $"[hero-raiding] no safe oasis after {readCount} map read(s), {cachedCount} cached area(s); next discovery follows the 60-minute retry schedule.");
            return new HeroRaidingDiscoveryResult(safe, seen.Values.ToList(), readCount, cachedCount);
        }
    }

    private async Task<string> ReadWithRetryAsync(int x, int y, int zoom, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { return await reader.ReadMapAreaAsync(x, y, zoom, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex.Message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Hero raiding map scan stopped because Travian denied the request or requested verification.", ex);
                if (attempt == 3) throw new InvalidOperationException($"Hero raiding could not read map area ({x}|{y}) at zoom {zoom} after 3 attempts.", ex);
                var rateLimited = ex.Message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase);
                var delay = TimeSpan.FromSeconds((rateLimited ? 30 : 2) * attempt + Random.Shared.NextDouble() * (rateLimited ? 30 : 2) * attempt);
                log($"[hero-raiding] map area ({x}|{y}) attempt {attempt}/3 failed: {ex.Message}; retrying in {delay.TotalSeconds:0.0}s.");
                await Task.Delay(delay, token);
            }
        }
        throw new InvalidOperationException("Hero raiding map scan could not finish.");
    }

    private List<CachedArea> LoadAreas(string path, DateTimeOffset now)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var cache = JsonSerializer.Deserialize<AreaCache>(File.ReadAllText(path));
            if (cache is null || !string.Equals(cache.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
                return [];
            return (cache.Areas ?? []).Where(area => area.Oases is not null && area.ZoomLevel is >= 1 and <= 3
                && area.ObservedAtUtc <= now
                && now - area.ObservedAtUtc <= TimeSpan.FromHours(24))
                .OrderBy(area => area.ObservedAtUtc).TakeLast(128).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"[hero-raiding] map cache unreadable; rescanning: {ex.Message}");
            return [];
        }
    }

    private sealed record AreaCache(string ServerUrl, List<CachedArea> Areas);
    private sealed record CachedArea(int CenterX, int CenterY, int ZoomLevel, DateTimeOffset ObservedAtUtc, List<MapOasisEntry> Oases);
}
