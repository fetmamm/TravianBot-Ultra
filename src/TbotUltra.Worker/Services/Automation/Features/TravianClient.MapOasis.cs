using TbotUltra.Worker.Services.Automation;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient : IMapOasisAreaReader
{
    internal async Task PrepareMapOasisScanAsync(CancellationToken cancellationToken)
    {
        await LoginAsync(cancellationToken);
        await GotoAsync(Paths.Map, cancellationToken);
        await WaitForPageReadyAsync(cancellationToken);
    }

    public Task<string> ReadMapAreaAsync(int x, int y, CancellationToken cancellationToken)
        => ReadMapAreaAsync(x, y, zoomLevel: 3, cancellationToken);

    public async Task<MapOasisApiCapture> CaptureCurrentMapAreaAsync(CancellationToken cancellationToken)
    {
        if (!IsCurrentUrlForPath(Paths.Map))
        {
            throw new InvalidOperationException("Open the map and center it on the area to capture first.");
        }

        var xText = await _page.Locator("#xCoordInputMap").InputValueAsync().WaitAsync(cancellationToken);
        var yText = await _page.Locator("#yCoordInputMap").InputValueAsync().WaitAsync(cancellationToken);
        if (!int.TryParse(xText, out var x) || !int.TryParse(yText, out var y))
        {
            throw new InvalidOperationException("Could not read the current map coordinates.");
        }

        var zoomLevel = 1;
        if (Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2
                    && string.Equals(parts[0], "zoom", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(Uri.UnescapeDataString(parts[1]), out var parsedZoom)
                    && parsedZoom is >= 1 and <= 4)
                {
                    zoomLevel = parsedZoom;
                    break;
                }
            }
        }

        var json = await ReadMapAreaAsync(x, y, zoomLevel, cancellationToken);
        Notify($"[map-oasis] captured map API area center=({x}|{y}) zoom={zoomLevel}; chars={json.Length}.");
        return new MapOasisApiCapture(_page.Url, x, y, zoomLevel, json);
    }

    public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken)
    {
        return _page.EvaluateAsync<string>(
            """
            async ({ x, y, zoomLevel }) => {
                const response = await fetch('/api/v1/map/position', {
                    method: 'POST', credentials: 'same-origin',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ data: { x, y, zoomLevel, ignorePositions: [] } })
                });
                const text = await response.text();
                if (!response.ok) throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
                return text;
            }
            """,
            new { x, y, zoomLevel }).WaitAsync(cancellationToken);
    }
}
