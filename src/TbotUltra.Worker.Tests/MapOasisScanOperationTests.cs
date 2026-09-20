using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class MapOasisScanOperationTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsFilteredOasesInExplicitResult()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-map-oasis-{Guid.NewGuid():N}");
        try
        {
            var reader = new ScriptedMapReader("""
                {"tiles":[
                  {"x":0,"y":0,"did":-1,"title":"{k.fo}","text":"{a:r1} {a.r1} 25%"},
                  {"x":1,"y":1,"did":-1,"uid":9,"title":"{k.bt}","text":"{a:r2} {a.r2} 25%"}
                ]}
                """);
            var operation = new MapOasisScanOperation(reader, root, "account", "https://ts1.x1.travian.com", _ => { });

            var result = await operation.ExecuteAsync(
                new MapOasisScanInput(
                    new MapOasisScanRequest(0, 0, MapOasisScanScope.Radius, 1, MapOasisScanSpeed.Fast),
                    IncludeOccupied: false,
                    SelectedTypes: ["Wood"]),
                progress: null,
                CancellationToken.None);

            var oasis = Assert.Single(result.Oases);
            Assert.Equal("Wood", oasis.FilterType);
            Assert.False(result.IsPartialResult);
            Assert.Equal(1, result.CompletedAreas);
            Assert.Equal(1, result.TotalAreas);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesDetailedMapZoomWhenRegionOverlayHidesOases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-map-oasis-region-{Guid.NewGuid():N}");
        try
        {
            var reader = new RegionMapReader();
            var operation = new MapOasisScanOperation(reader, root, "account", "https://rog.x5.international.travian.com", _ => { });

            var result = await operation.ExecuteAsync(
                new MapOasisScanInput(
                    new MapOasisScanRequest(-187, -185, MapOasisScanScope.Radius, 1, MapOasisScanSpeed.Fast),
                    IncludeOccupied: true,
                    SelectedTypes: ["Clay+Crop"]),
                progress: null,
                CancellationToken.None);

            var oasis = Assert.Single(result.Oases);
            Assert.Equal((-187, -185), (oasis.X, oasis.Y));
            Assert.Equal("Clay+Crop", oasis.FilterType);
            Assert.Contains(3, reader.ZoomLevels);
            Assert.Contains(2, reader.ZoomLevels);
            Assert.DoesNotContain(1, reader.ZoomLevels);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToZoomOneWhenZoomTwoStillReturnsRegionOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-map-oasis-region-fallback-{Guid.NewGuid():N}");
        try
        {
            var reader = new RegionMapReader(detailedZoomLevel: 1);
            var operation = new MapOasisScanOperation(reader, root, "account", "https://rog.x5.international.travian.com", _ => { });

            var result = await operation.ExecuteAsync(
                new MapOasisScanInput(
                    new MapOasisScanRequest(-187, -185, MapOasisScanScope.Radius, 1, MapOasisScanSpeed.Fast),
                    IncludeOccupied: true,
                    SelectedTypes: ["Clay+Crop"]),
                progress: null,
                CancellationToken.None);

            Assert.Single(result.Oases);
            Assert.Equal([3, 2, 1], reader.ZoomLevels);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_StopsImmediatelyWhenTravianDeniesMapRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-map-oasis-denied-{Guid.NewGuid():N}");
        try
        {
            var reader = new RejectingMapReader();
            var operation = new MapOasisScanOperation(reader, root, "account", "https://example.com", _ => { });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteAsync(
                new MapOasisScanInput(
                    new MapOasisScanRequest(0, 0, MapOasisScanScope.Radius, 1, MapOasisScanSpeed.Fast),
                    IncludeOccupied: true,
                    SelectedTypes: ["Crop"]),
                progress: null,
                CancellationToken.None));

            Assert.Contains("denied the request", error.Message);
            Assert.Equal(1, reader.CallCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedMapReader(string json) : IMapOasisAreaReader
    {
        public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken) => Task.FromResult(json);
    }

    private sealed class RegionMapReader(int detailedZoomLevel = 2) : IMapOasisAreaReader
    {
        public List<int> ZoomLevels { get; } = [];

        public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken)
        {
            ZoomLevels.Add(zoomLevel);
            return Task.FromResult(zoomLevel == detailedZoomLevel
                ? """{"tiles":[{"position":{"x":-187,"y":-185},"did":-1,"title":"{k.fo}","text":"{k.regionTooltip} Volubilis<br />{a:r2} {a.r2} 25%<br />{a:r4} {a.r4} 25%"}]}"""
                : """{"tiles":[{"position":{"x":-187,"y":-185},"title":"{k.regionTooltip} Volubilis","text":"The eagles slight eyes VII"}]}""");
        }
    }

    private sealed class RejectingMapReader : IMapOasisAreaReader
    {
        public int CallCount { get; private set; }

        public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("HTTP 403: forbidden");
        }
    }
}
