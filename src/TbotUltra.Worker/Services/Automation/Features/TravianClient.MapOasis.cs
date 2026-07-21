using Microsoft.Playwright;
using System.Text.Json;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    public async Task<IReadOnlyList<MapOasisEntry>> ScanMapOasesAsync(
        bool includeOccupied,
        IReadOnlyCollection<string> selectedTypes,
        MapOasisScanRequest request,
        IProgress<MapOasisScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (selectedTypes.Count == 0)
        {
            throw new InvalidOperationException("Select at least one oasis type.");
        }

        if (request.Scope == MapOasisScanScope.Radius
            && (request.Radius < 1 || request.Radius > 200))
        {
            throw new InvalidOperationException("Map oasis radius must be between 1 and 200.");
        }

        await LoginAsync(cancellationToken);
        await GotoAsync(Paths.Map, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);

        var minimumX = request.Scope == MapOasisScanScope.Radius
            ? Math.Max(-200, request.CenterX - request.Radius)
            : -200;
        var maximumX = request.Scope == MapOasisScanScope.Radius
            ? Math.Min(200, request.CenterX + request.Radius)
            : 200;
        var minimumY = request.Scope == MapOasisScanScope.Radius
            ? Math.Max(-200, request.CenterY - request.Radius)
            : -200;
        var maximumY = request.Scope == MapOasisScanScope.Radius
            ? Math.Min(200, request.CenterY + request.Radius)
            : 200;
        if (minimumX > maximumX || minimumY > maximumY)
        {
            throw new InvalidOperationException("Map oasis center must be within the map boundaries.");
        }

        var centers = Automation.MapOasisApiParser.CreateScanCenters(minimumX, maximumX, minimumY, maximumY);
        var selected = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
        var filterKey = string.Join("|", selected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            + $";scope={request.Scope};center={request.CenterX}|{request.CenterY};radius={request.Radius};speed={request.Speed}";
        var checkpointPath = AccountStoragePaths.MapOasisCheckpointPath(_projectRoot, AccountName, ServerUrl);
        var checkpoint = await LoadMapOasisCheckpointAsync(
            checkpointPath,
            filterKey,
            includeOccupied,
            cancellationToken);
        var completedAreas = checkpoint?.CompletedAreas.ToHashSet() ?? [];
        var found = (checkpoint?.Oases ?? [])
            .ToDictionary(oasis => (oasis.X, oasis.Y));
        Notify(
            $"[map-oasis] scanning {centers.Count} map areas with zoom level 3; " +
            $"scope={request.Scope}; center=({request.CenterX}|{request.CenterY}); radius={request.Radius}; speed={request.Speed}.");
        if (completedAreas.Count > 0)
        {
            Notify($"[map-oasis] resuming checkpoint with {completedAreas.Count}/{centers.Count} completed areas.");
            progress?.Report(new MapOasisScanProgress(completedAreas.Count, centers.Count, found.Count));
        }

        try
        {
            var diagnosticLogged = false;
            for (var index = 0; index < centers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completedAreas.Contains(index))
                {
                    continue;
                }

                var center = centers[index];
                var json = await ReadMapAreaWithRetryAsync(center.X, center.Y, cancellationToken);
                if (!diagnosticLogged)
                {
                    diagnosticLogged = true;
                    LogMapAreaDiagnostic(json, center.X, center.Y);
                }

                foreach (var oasis in Automation.MapOasisApiParser.Parse(json))
                {
                    if (oasis.X is < -200 or > 200
                        || oasis.Y is < -200 or > 200
                        || oasis.X < minimumX
                        || oasis.X > maximumX
                        || oasis.Y < minimumY
                        || oasis.Y > maximumY
                        || (!includeOccupied && oasis.IsOccupied)
                        || !selected.Contains(oasis.FilterType))
                    {
                        continue;
                    }

                    found[(oasis.X, oasis.Y)] = oasis;
                }

                completedAreas.Add(index);
                var completed = completedAreas.Count;
                progress?.Report(new MapOasisScanProgress(completed, centers.Count, found.Count));
                if (completed == 1 || completed % 10 == 0 || completed == centers.Count)
                {
                    Notify($"[map-oasis] scanned {completed}/{centers.Count} areas; {found.Count} matching oases.");
                }

                if (completed % 5 == 0)
                {
                    await SaveMapOasisSnapshotAsync(
                        checkpointPath,
                        filterKey,
                        includeOccupied,
                        completedAreas,
                        found.Values,
                        cancellationToken);
                }

                if (completed < centers.Count)
                {
                    await ApplyMapOasisDelayAsync(request.Speed, completed, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            var partialResult = found.Values
                .OrderBy(oasis => oasis.Y)
                .ThenBy(oasis => oasis.X)
                .ToList();
            // A user cancel keeps the completed work as a local list, but a later scan starts fresh.
            // Local persistence must not inherit the canceled operation token.
            await SaveMapOasisSnapshotAsync(
                AccountStoragePaths.MapOasisCachePath(_projectRoot, AccountName, ServerUrl),
                filterKey,
                includeOccupied,
                completedAreas,
                partialResult,
                CancellationToken.None);
            DeleteMapOasisCheckpoint(checkpointPath);
            progress?.Report(new MapOasisScanProgress(
                completedAreas.Count,
                centers.Count,
                partialResult.Count,
                IsPartialResult: true));
            Notify($"[map-oasis] scan canceled; saved {partialResult.Count} oasis/oases from {completedAreas.Count}/{centers.Count} completed areas.");
            return partialResult;
        }
        catch
        {
            // Unexpected failures keep the checkpoint so the scan can resume after a retry.
            // Deliberately independent of the operation token: this is bounded local file I/O,
            // does not hold the browser session gate, and must preserve progress after a failure.
            try
            {
                await SaveMapOasisSnapshotAsync(
                    checkpointPath,
                    filterKey,
                    includeOccupied,
                    completedAreas,
                    found.Values,
                    // Preserve the last completed map areas even when the scan itself was canceled.
                    CancellationToken.None);
            }
            catch (Exception checkpointException)
            {
                Notify($"[map-oasis] checkpoint save failed: {checkpointException.Message}");
            }

            throw;
        }

        var result = found.Values
            .OrderBy(oasis => oasis.Y)
            .ThenBy(oasis => oasis.X)
            .ToList();
        await SaveMapOasisSnapshotAsync(
            AccountStoragePaths.MapOasisCachePath(_projectRoot, AccountName, ServerUrl),
            filterKey,
            includeOccupied,
            completedAreas,
            result,
            cancellationToken);
        DeleteMapOasisCheckpoint(checkpointPath);
        Notify($"[map-oasis] scan completed with {result.Count} matching oases.");
        return result;
    }

    private void DeleteMapOasisCheckpoint(string checkpointPath)
    {
        try
        {
            if (File.Exists(checkpointPath))
            {
                File.Delete(checkpointPath);
            }
        }
        catch (Exception ex)
        {
            Notify($"[map-oasis] checkpoint delete failed: {ex.Message}");
        }
    }

    private async Task ApplyMapOasisDelayAsync(
        MapOasisScanSpeed speed,
        int completedAreas,
        CancellationToken cancellationToken)
    {
        var (minimumSeconds, maximumSeconds, breakInterval, breakMinimumSeconds, breakMaximumSeconds) =
            speed == MapOasisScanSpeed.Fast
                ? (0.8, 1.8, 20, 2.0, 6.0)
                : (1.5, 3.5, 12, 6.0, 15.0);

        var delaySeconds = Random.Shared.NextDouble() * (maximumSeconds - minimumSeconds) + minimumSeconds;
        Notify($"[map-oasis] {speed} pacing: waiting {delaySeconds:0.0}s before the next map area.");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

        if (completedAreas % breakInterval != 0)
        {
            return;
        }

        var breakSeconds = Random.Shared.NextDouble() * (breakMaximumSeconds - breakMinimumSeconds) + breakMinimumSeconds;
        Notify($"[map-oasis] {speed} pacing break after {completedAreas} areas: waiting {breakSeconds:0.0}s.");
        await Task.Delay(TimeSpan.FromSeconds(breakSeconds), cancellationToken);
    }

    private async Task<MapOasisSnapshot?> LoadMapOasisCheckpointAsync(
        string path,
        string filterKey,
        bool includeOccupied,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<MapOasisSnapshot>(
                stream,
                cancellationToken: cancellationToken);
            if (snapshot is null
                || !string.Equals(snapshot.ServerUrl, ServerUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(snapshot.FilterKey, filterKey, StringComparison.Ordinal)
                || snapshot.IncludeOccupied != includeOccupied)
            {
                return null;
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notify($"[map-oasis] checkpoint could not be loaded and will be ignored: {ex.Message}");
            return null;
        }
    }

    private async Task SaveMapOasisSnapshotAsync(
        string path,
        string filterKey,
        bool includeOccupied,
        IEnumerable<int> completedAreas,
        IEnumerable<MapOasisEntry> oases,
        CancellationToken cancellationToken)
    {
        var snapshot = new MapOasisSnapshot
        {
            ServerUrl = ServerUrl,
            FilterKey = filterKey,
            IncludeOccupied = includeOccupied,
            UpdatedUtc = DateTimeOffset.UtcNow,
            CompletedAreas = completedAreas.Order().ToList(),
            Oases = oases.OrderBy(oasis => oasis.Y).ThenBy(oasis => oasis.X).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private void LogMapAreaDiagnostic(string json, int x, int y)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Array)
            {
                var keys = string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name));
                Notify($"[map-oasis:diagnostic] area ({x}|{y}): no 'tiles' array. Root keys: [{keys}]");
                Notify($"[map-oasis:diagnostic] raw snippet: {json[..Math.Min(json.Length, 300)]}");
                return;
            }

            var tileList = tiles.EnumerateArray().ToList();
            var didMinusOne = tileList
                .Where(t => t.TryGetProperty("did", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == -1)
                .ToList();
            var titleGroups = didMinusOne
                .GroupBy(t => t.TryGetProperty("title", out var ti) ? (ti.GetString() ?? "null") : "(no title)")
                .Select(g => $"'{g.Key}'×{g.Count()}")
                .ToList();

            Notify($"[map-oasis:diagnostic] area ({x}|{y}): total tiles={tileList.Count}, did=-1 count={didMinusOne.Count}, titles=[{string.Join(", ", titleGroups)}]");

            var freeOasis = didMinusOne.FirstOrDefault(t =>
                t.TryGetProperty("title", out var ti) && ti.GetString() == "{k.fo}");
            var occupiedOasis = didMinusOne.FirstOrDefault(t =>
                t.TryGetProperty("title", out var ti) && ti.GetString() == "{k.bt}");

            if (freeOasis.ValueKind != JsonValueKind.Undefined)
            {
                LogOasisTileSample("free", freeOasis);
            }

            if (occupiedOasis.ValueKind != JsonValueKind.Undefined)
            {
                LogOasisTileSample("occupied", occupiedOasis);
            }

            if (freeOasis.ValueKind == JsonValueKind.Undefined
                && occupiedOasis.ValueKind == JsonValueKind.Undefined
                && didMinusOne.Count > 0)
            {
                var sample = didMinusOne[0];
                var title = sample.TryGetProperty("title", out var ti) ? (ti.GetString() ?? "(null)") : "(no title)";
                var text = sample.TryGetProperty("text", out var te) ? (te.GetString() ?? "(null)") : "(no text)";
                Notify($"[map-oasis:diagnostic] sample did=-1 tile: title='{title}', text='{text}'");
                Notify($"[map-oasis:diagnostic] raw snippet: {json[..Math.Min(json.Length, 500)]}");
            }
            else if (tileList.Count > 0)
            {
                var first = tileList[0];
                var fieldKeys = string.Join(", ", first.EnumerateObject().Select(p => p.Name).Take(12));
                var sampleDids = string.Join(", ", tileList.Take(5)
                    .Select(t => t.TryGetProperty("did", out var d) ? d.ToString() : "(-)"));
                Notify($"[map-oasis:diagnostic] no did=-1 tiles found. Total={tileList.Count}. Keys: [{fieldKeys}]. Sample dids: [{sampleDids}]");
                Notify($"[map-oasis:diagnostic] raw snippet: {json[..Math.Min(json.Length, 500)]}");
            }
            else
            {
                Notify($"[map-oasis:diagnostic] area ({x}|{y}): tiles array is empty.");
                Notify($"[map-oasis:diagnostic] raw: {json[..Math.Min(json.Length, 300)]}");
            }
        }
        catch (Exception ex)
        {
            Notify($"[map-oasis:diagnostic] parse failed for ({x}|{y}): {ex.Message}");
            Notify($"[map-oasis:diagnostic] raw: {json[..Math.Min(json.Length, 200)]}");
        }
    }

    private void LogOasisTileSample(string label, JsonElement tile)
    {
        var tileKeys = string.Join(", ", tile.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}"));
        Notify($"[map-oasis:diagnostic] {label} oasis tile keys: [{tileKeys}]");
        var raw = tile.GetRawText();
        Notify($"[map-oasis:diagnostic] {label} oasis tile raw: {raw[..Math.Min(raw.Length, 1500)]}");
    }

    private async Task<string> ReadMapAreaWithRetryAsync(int x, int y, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await _page.EvaluateAsync<string>(
                    """
                    async ({ x, y }) => {
                        const response = await fetch('/api/v1/map/position', {
                            method: 'POST',
                            credentials: 'same-origin',
                            headers: { 'content-type': 'application/json' },
                            body: JSON.stringify({
                                data: { x, y, zoomLevel: 3, ignorePositions: [] }
                            })
                        });
                        const text = await response.text();
                        if (!response.ok) {
                            throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
                        }
                        return text;
                    }
                    """,
                    new { x, y }).WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                Notify($"[map-oasis] area ({x}|{y}) attempt {attempt}/{attempts} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException($"Could not read map area centered at ({x}|{y}) after {attempts} attempts.");
    }

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
