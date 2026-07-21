using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Infrastructure;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services.Automation;

internal interface IMapOasisAreaReader
{
    Task<string> ReadMapAreaAsync(int x, int y, CancellationToken cancellationToken);
}

internal sealed class MapOasisScanOperation(
    IMapOasisAreaReader reader,
    string projectRoot,
    string accountName,
    string serverUrl,
    Action<string> log)
{
    public async Task<MapOasisScanResult> ExecuteAsync(
        MapOasisScanInput input,
        IProgress<MapOasisScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (input.SelectedTypes.Count == 0)
        {
            throw new InvalidOperationException("Select at least one oasis type.");
        }

        var request = input.Request;
        if (request.Scope == MapOasisScanScope.Radius && (request.Radius < 1 || request.Radius > 200))
        {
            throw new InvalidOperationException("Map oasis radius must be between 1 and 200.");
        }

        var minimumX = request.Scope == MapOasisScanScope.Radius ? Math.Max(-200, request.CenterX - request.Radius) : -200;
        var maximumX = request.Scope == MapOasisScanScope.Radius ? Math.Min(200, request.CenterX + request.Radius) : 200;
        var minimumY = request.Scope == MapOasisScanScope.Radius ? Math.Max(-200, request.CenterY - request.Radius) : -200;
        var maximumY = request.Scope == MapOasisScanScope.Radius ? Math.Min(200, request.CenterY + request.Radius) : 200;
        if (minimumX > maximumX || minimumY > maximumY)
        {
            throw new InvalidOperationException("Map oasis center must be within the map boundaries.");
        }

        var centers = MapOasisApiParser.CreateScanCenters(minimumX, maximumX, minimumY, maximumY);
        var selected = new HashSet<string>(input.SelectedTypes, StringComparer.OrdinalIgnoreCase);
        var filterKey = string.Join("|", selected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            + $";scope={request.Scope};center={request.CenterX}|{request.CenterY};radius={request.Radius};speed={request.Speed}";
        var checkpointPath = AccountStoragePaths.MapOasisCheckpointPath(projectRoot, accountName, serverUrl);
        var checkpoint = await LoadSnapshotAsync(checkpointPath, filterKey, input.IncludeOccupied, cancellationToken);
        var completedAreas = checkpoint?.CompletedAreas.ToHashSet() ?? [];
        var found = (checkpoint?.Oases ?? []).ToDictionary(oasis => (oasis.X, oasis.Y));
        log($"[map-oasis] scanning {centers.Count} map areas with zoom level 3; scope={request.Scope}; center=({request.CenterX}|{request.CenterY}); radius={request.Radius}; speed={request.Speed}.");

        try
        {
            for (var index = 0; index < centers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completedAreas.Contains(index)) continue;
                var center = centers[index];
                var json = await ReadWithRetryAsync(center.X, center.Y, cancellationToken);
                foreach (var oasis in MapOasisApiParser.Parse(json))
                {
                    if (oasis.X is < -200 or > 200 || oasis.Y is < -200 or > 200
                        || oasis.X < minimumX || oasis.X > maximumX || oasis.Y < minimumY || oasis.Y > maximumY
                        || (!input.IncludeOccupied && oasis.IsOccupied) || !selected.Contains(oasis.FilterType)) continue;
                    found[(oasis.X, oasis.Y)] = oasis;
                }

                completedAreas.Add(index);
                progress?.Report(new MapOasisScanProgress(completedAreas.Count, centers.Count, found.Count));
                if (completedAreas.Count % 5 == 0)
                {
                    await SaveSnapshotAsync(checkpointPath, filterKey, input.IncludeOccupied, completedAreas, found.Values, cancellationToken);
                }
                if (completedAreas.Count < centers.Count) await ApplyDelayAsync(request.Speed, completedAreas.Count, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            var partial = Order(found.Values);
            await SaveSnapshotAsync(AccountStoragePaths.MapOasisCachePath(projectRoot, accountName, serverUrl), filterKey, input.IncludeOccupied, completedAreas, partial, CancellationToken.None);
            DeleteCheckpoint(checkpointPath);
            progress?.Report(new MapOasisScanProgress(completedAreas.Count, centers.Count, partial.Count, true));
            log($"[map-oasis] scan canceled; saved {partial.Count} oasis/oases from {completedAreas.Count}/{centers.Count} completed areas.");
            return new MapOasisScanResult(partial, completedAreas.Count, centers.Count, true);
        }
        catch
        {
            try { await SaveSnapshotAsync(checkpointPath, filterKey, input.IncludeOccupied, completedAreas, found.Values, CancellationToken.None); }
            catch (Exception ex) { log($"[map-oasis] checkpoint save failed: {ex.Message}"); }
            throw;
        }

        var result = Order(found.Values);
        await SaveSnapshotAsync(AccountStoragePaths.MapOasisCachePath(projectRoot, accountName, serverUrl), filterKey, input.IncludeOccupied, completedAreas, result, cancellationToken);
        DeleteCheckpoint(checkpointPath);
        log($"[map-oasis] scan completed with {result.Count} matching oases.");
        return new MapOasisScanResult(result, completedAreas.Count, centers.Count);
    }

    private async Task<string> ReadWithRetryAsync(int x, int y, CancellationToken token)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { return await reader.ReadMapAreaAsync(x, y, token); }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                log($"[map-oasis] area ({x}|{y}) attempt {attempt}/3 failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), token);
            }
        }
        throw new InvalidOperationException($"Could not read map area centered at ({x}|{y}) after 3 attempts.");
    }

    private async Task ApplyDelayAsync(MapOasisScanSpeed speed, int completed, CancellationToken token)
    {
        var (min, max, interval, breakMin, breakMax) = speed == MapOasisScanSpeed.Fast ? (0.8, 1.8, 20, 2.0, 6.0) : (1.5, 3.5, 12, 6.0, 15.0);
        var seconds = Random.Shared.NextDouble() * (max - min) + min;
        log($"[map-oasis] {speed} pacing: waiting {seconds:0.0}s before the next map area.");
        await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        if (completed % interval != 0) return;
        seconds = Random.Shared.NextDouble() * (breakMax - breakMin) + breakMin;
        log($"[map-oasis] {speed} pacing break after {completed} areas: waiting {seconds:0.0}s.");
        await Task.Delay(TimeSpan.FromSeconds(seconds), token);
    }

    private async Task<MapOasisSnapshot?> LoadSnapshotAsync(string path, string filterKey, bool includeOccupied, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<MapOasisSnapshot>(stream, cancellationToken: token);
            return snapshot is not null && string.Equals(snapshot.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.FilterKey, filterKey, StringComparison.Ordinal) && snapshot.IncludeOccupied == includeOccupied ? snapshot : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"[map-oasis] checkpoint could not be loaded and will be ignored: {ex.Message}");
            return null;
        }
    }

    private Task SaveSnapshotAsync(string path, string filterKey, bool includeOccupied, IEnumerable<int> completed, IEnumerable<MapOasisEntry> oases, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = new MapOasisSnapshot { ServerUrl = serverUrl, FilterKey = filterKey, IncludeOccupied = includeOccupied, UpdatedUtc = DateTimeOffset.UtcNow, CompletedAreas = completed.Order().ToList(), Oases = Order(oases) };
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        return Task.CompletedTask;
    }

    private void DeleteCheckpoint(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log($"[map-oasis] checkpoint delete failed: {ex.Message}"); }
    }

    private static List<MapOasisEntry> Order(IEnumerable<MapOasisEntry> oases) => oases.OrderBy(oasis => oasis.Y).ThenBy(oasis => oasis.X).ToList();

    private sealed class MapOasisSnapshot
    {
        public string ServerUrl { get; init; } = string.Empty;
        public string FilterKey { get; init; } = string.Empty;
        public bool IncludeOccupied { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
        public List<int> CompletedAreas { get; init; } = [];
        public List<MapOasisEntry> Oases { get; init; } = [];
    }
}
