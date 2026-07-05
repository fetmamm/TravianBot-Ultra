using TbotUltra.Core.Configuration;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ContinuousLoopTroopTrainingTests
{
    [Fact]
    public void ShouldGateTroopTrainingEnqueueOnActiveQueue_NoLimitDoesNotGate()
    {
        var options = new BotOptions
        {
            TroopTrainingBarracksEnabled = true,
            TroopTrainingBarracksMaxQueueHours = "no_limit"
        };

        Assert.False(MainWindow.ShouldGateTroopTrainingEnqueueOnActiveQueue(options));
    }

    [Fact]
    public void ShouldGateTroopTrainingEnqueueOnActiveQueue_EnabledNumericLimitGates()
    {
        var options = new BotOptions
        {
            TroopTrainingBarracksEnabled = true,
            TroopTrainingBarracksMaxQueueHours = "10"
        };

        Assert.True(MainWindow.ShouldGateTroopTrainingEnqueueOnActiveQueue(options));
    }

    [Fact]
    public void ShouldGateTroopTrainingEnqueueOnActiveQueue_DisabledNumericLimitDoesNotGate()
    {
        var options = new BotOptions
        {
            TroopTrainingBarracksEnabled = false,
            TroopTrainingBarracksMaxQueueHours = "10"
        };

        Assert.False(MainWindow.ShouldGateTroopTrainingEnqueueOnActiveQueue(options));
    }
}
