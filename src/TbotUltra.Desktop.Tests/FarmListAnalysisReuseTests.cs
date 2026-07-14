using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class FarmListAnalysisReuseTests
{
    [Fact]
    public void CanReuseRecentFarmListAnalysis_AcceptsFreshAnalysis()
    {
        var now = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);

        Assert.True(MainWindow.CanReuseRecentFarmListAnalysis(now.AddSeconds(-42), now));
    }

    [Fact]
    public void CanReuseRecentFarmListAnalysis_RejectsMissingOrStaleAnalysis()
    {
        var now = new DateTimeOffset(2026, 7, 14, 1, 0, 0, TimeSpan.Zero);

        Assert.False(MainWindow.CanReuseRecentFarmListAnalysis(DateTimeOffset.MinValue, now));
        Assert.False(MainWindow.CanReuseRecentFarmListAnalysis(now.AddMinutes(-6), now));
    }
}
