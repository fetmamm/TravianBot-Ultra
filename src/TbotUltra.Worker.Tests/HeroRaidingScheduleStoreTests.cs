using TbotUltra.Core.Accounts;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class HeroRaidingScheduleStoreTests
{
    [Fact]
    public void NoCandidateWaitsSixtyMinutesAndSuccessfulScanClearsGate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-schedule-{Guid.NewGuid():N}");
        try
        {
            var store = new HeroRaidingScheduleStore(root, _ => { });
            var now = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

            Assert.True(store.IsDiscoveryDue("a", "https://ts1.travian.com", now));
            Assert.Equal(now.AddHours(1), store.RecordNoCandidate("a", "https://ts1.travian.com", now));
            Assert.False(store.IsDiscoveryDue("a", "https://ts1.travian.com", now.AddMinutes(59)));
            Assert.True(store.IsDiscoveryDue("a", "https://ts1.travian.com", now.AddHours(1)));
            Assert.True(store.IsDiscoveryDue("b", "https://ts1.travian.com", now));
            Assert.True(store.IsDiscoveryDue("a", "https://ts2.travian.com", now));

            store.ClearAfterSuccessfulScan("a", "https://ts1.travian.com");
            Assert.True(store.IsDiscoveryDue("a", "https://ts1.travian.com", now));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptScheduleDoesNotPermitRepeatedScan()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-hero-schedule-{Guid.NewGuid():N}");
        try
        {
            var path = AccountStoragePaths.HeroRaidingSchedulePath(root, "a", "https://ts1.travian.com");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{invalid");
            var store = new HeroRaidingScheduleStore(root, _ => { });

            Assert.Throws<InvalidOperationException>(() =>
                store.IsDiscoveryDue("a", "https://ts1.travian.com", DateTimeOffset.UtcNow));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
