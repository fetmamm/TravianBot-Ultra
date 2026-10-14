using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingPendingOutcomeStoreTests
{
    [Fact]
    public void PendingRaidSurvivesRestartAndStaysAccountWorldScoped()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-outcome-{Guid.NewGuid():N}");
        try
        {
            var saved = new HeroRaidingPendingOutcome(1, 2, 3, 4, 100, 65, 40,
                new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 14, 12, 30, 0, TimeSpan.Zero), true);
            new HeroRaidingPendingOutcomeStore(root, _ => { })
                .SaveSent("a", "https://ts1.travian.com", saved);

            var restored = new HeroRaidingPendingOutcomeStore(root, _ => { });
            Assert.Equal(saved, restored.Load("a", "https://ts1.travian.com"));
            Assert.Null(restored.Load("a", "https://ts2.travian.com"));
            Assert.Null(restored.Load("b", "https://ts1.travian.com"));

            restored.MarkSafetyDisabled("a", "https://ts1.travian.com", saved);
            Assert.True(restored.Load("a", "https://ts1.travian.com")!.SafetyDisabled);

            restored.ClearVerified("a", "https://ts1.travian.com");
            Assert.Null(restored.Load("a", "https://ts1.travian.com"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptPendingOutcomeBlocksNewRaids()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-outcome-{Guid.NewGuid():N}");
        try
        {
            var path = AccountStoragePaths.HeroRaidingPendingOutcomePath(root, "a", "https://ts1.travian.com");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{invalid");

            Assert.Throws<InvalidOperationException>(() =>
                new HeroRaidingPendingOutcomeStore(root, _ => { })
                    .Load("a", "https://ts1.travian.com"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
