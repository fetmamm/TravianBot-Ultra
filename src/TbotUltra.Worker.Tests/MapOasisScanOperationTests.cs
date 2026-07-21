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

    private sealed class ScriptedMapReader(string json) : IMapOasisAreaReader
    {
        public Task<string> ReadMapAreaAsync(int x, int y, CancellationToken cancellationToken) => Task.FromResult(json);
    }
}
