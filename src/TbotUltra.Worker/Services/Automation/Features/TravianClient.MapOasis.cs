using TbotUltra.Worker.Services.Automation;

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
    {
        return _page.EvaluateAsync<string>(
            """
            async ({ x, y }) => {
                const response = await fetch('/api/v1/map/position', {
                    method: 'POST', credentials: 'same-origin',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify({ data: { x, y, zoomLevel: 3, ignorePositions: [] } })
                });
                const text = await response.text();
                if (!response.ok) throw new Error(`HTTP ${response.status}: ${text.slice(0, 200)}`);
                return text;
            }
            """,
            new { x, y }).WaitAsync(cancellationToken);
    }
}
