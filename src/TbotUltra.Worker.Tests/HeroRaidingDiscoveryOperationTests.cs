using TbotUltra.Worker.Services.Automation;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingDiscoveryOperationTests
{
    private const string RatOasis = """
        {"tiles":[{"x":1,"y":0,"did":-1,"title":"{k.fo}","text":"{a:r1} {a.r1} 25%<br />{k.animals}<br /><div class=\"inlineIcon tooltipUnit\" title=\"\"><i class=\"unit u31\"></i><span class=\"value \">2</span></div>"}]}
        """;

    [Fact]
    public async Task ExecuteAsync_StopsAfterFirstSafeAreaAndReusesFreshCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-discovery-{Guid.NewGuid():N}");
        try
        {
            var reader = new ScriptedReader(RatOasis);
            var operation = new HeroRaidingDiscoveryOperation(reader, root, "account", "https://ts1.travian.com", _ => { });
            var input = Input();

            var first = await operation.ExecuteAsync(input, CancellationToken.None);
            var second = await operation.ExecuteAsync(input, CancellationToken.None);

            Assert.Single(first.SafeCandidates);
            Assert.Equal((1, 0), (first.SafeCandidates[0].Candidate.Oasis.X, first.SafeCandidates[0].Candidate.Oasis.Y));
            Assert.Equal(1, first.AreasRead);
            Assert.Equal(0, second.AreasRead);
            Assert.Equal(1, second.CachedAreasUsed);
            Assert.Equal(1, reader.CallCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExclusionPreventsSafeTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-discovery-{Guid.NewGuid():N}");
        try
        {
            var reader = new ScriptedReader(RatOasis);
            var operation = new HeroRaidingDiscoveryOperation(reader, root, "account", "https://ts1.travian.com", _ => { });
            var input = Input() with { Excluded = new HashSet<(int X, int Y)> { (1, 0) } };

            var result = await operation.ExecuteAsync(input, CancellationToken.None);

            Assert.Empty(result.SafeCandidates);
            Assert.Single(result.ObservedOases);
            Assert.Equal(1, result.AreasRead);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackFromRegionOverlayToDetailedZoom()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-discovery-{Guid.NewGuid():N}");
        try
        {
            var reader = new ScriptedReader(RatOasis, overlayAtZoomThree: true);
            var operation = new HeroRaidingDiscoveryOperation(reader, root, "account", "https://ts1.travian.com", _ => { });

            var result = await operation.ExecuteAsync(Input(), CancellationToken.None);

            Assert.Single(result.SafeCandidates);
            Assert.Equal(2, result.AreasRead);
            Assert.Equal([3, 2], reader.ZoomLevels);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HeroRaidingDiscoveryInput Input() => new(
        HeroVillageX: 0,
        HeroVillageY: 0,
        MaximumDistance: 1,
        ObservationMinutes: 10,
        SelectedTypes: ["Wood"],
        Excluded: new HashSet<(int X, int Y)>(),
        HeroAttackStrength: 8100,
        CurrentHpPercent: 100,
        HeroMounted: false,
        MinimumHpPercent: 40);

    private sealed class ScriptedReader(string json, bool overlayAtZoomThree = false) : IMapOasisAreaReader
    {
        public int CallCount { get; private set; }
        public List<int> ZoomLevels { get; } = [];

        public Task<string> ReadMapAreaAsync(int x, int y, int zoomLevel, CancellationToken cancellationToken)
        {
            CallCount++;
            ZoomLevels.Add(zoomLevel);
            return Task.FromResult(overlayAtZoomThree && zoomLevel == 3
                ? """{"tiles":[{"x":0,"y":0,"title":"{k.regionTooltip} Region"}]}"""
                : json);
        }
    }
}
