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
        IProgress<MapOasisScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_config.IsPrivateServer)
        {
            throw new InvalidOperationException("Map oasis analysis supports official Travian servers only.");
        }

        if (selectedTypes.Count == 0)
        {
            throw new InvalidOperationException("Select at least one oasis type.");
        }

        await LoginAsync(cancellationToken);
        await GotoAsync("/karte.php", cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);

        var centers = Automation.MapOasisApiParser.CreateScanCenters();
        var selected = new HashSet<string>(selectedTypes, StringComparer.OrdinalIgnoreCase);
        var filterKey = string.Join("|", selected.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        var checkpointPath = AccountStoragePaths.MapOasisCheckpointPath(_projectRoot, AccountName, ServerUrl);
        var checkpoint = await LoadMapOasisCheckpointAsync(
            checkpointPath,
            filterKey,
            includeOccupied,
            cancellationToken);
        var completedAreas = checkpoint?.CompletedAreas.ToHashSet() ?? [];
        var found = (checkpoint?.Oases ?? [])
            .ToDictionary(oasis => (oasis.X, oasis.Y));
        Notify($"[map-oasis] scanning {centers.Count} map areas with zoom level 3.");
        if (completedAreas.Count > 0)
        {
            Notify($"[map-oasis] resuming checkpoint with {completedAreas.Count}/{centers.Count} completed areas.");
            progress?.Report(new MapOasisScanProgress(completedAreas.Count, centers.Count, found.Count));
        }

        try
        {
            for (var index = 0; index < centers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completedAreas.Contains(index))
                {
                    continue;
                }

                var center = centers[index];
                var json = await ReadMapAreaWithRetryAsync(center.X, center.Y, cancellationToken);
                foreach (var oasis in Automation.MapOasisApiParser.Parse(json))
                {
                    if (oasis.X is < -200 or > 200
                        || oasis.Y is < -200 or > 200
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
                    await Task.Delay(300, cancellationToken);
                }
            }
        }
        catch
        {
            try
            {
                await SaveMapOasisSnapshotAsync(
                    checkpointPath,
                    filterKey,
                    includeOccupied,
                    completedAreas,
                    found.Values,
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
        File.Delete(checkpointPath);
        Notify($"[map-oasis] scan completed with {result.Count} matching oases.");
        return result;
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
