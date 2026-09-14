using TbotUltra.Core.Configuration;
using TbotUltra.Core.Tasks;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class TroopTrainingExecutionSettingsTests
{
    [Theory]
    [InlineData(QueueStatus.Pending)]
    [InlineData(QueueStatus.Paused)]
    [InlineData(QueueStatus.Running)]
    public void Resolve_UsesSavedPhalanxInsteadOfQueuedSwordsman(QueueStatus status)
    {
        var item = new QueueItem
        {
            TaskName = "build_troops", Status = status,
            Payload = Settings("Swordsman").ToDictionary(),
            NextAttemptAt = DateTimeOffset.UtcNow.AddHours(2),
        };
        item.Payload[BotOptionPayloadKeys.TargetVillageKey] = "(1|2)";
        var deadline = item.NextAttemptAt;
        using var scope = TroopTrainingExecutionSettings.BeginScope(() => Settings("Phalanx"), _ => { });
        var resolved = QueueExecutionOptionsResolver.Resolve(new BotOptions(), item);
        Assert.Equal("Phalanx", resolved.TroopTrainingBarracksTroopType);
        Assert.True(resolved.TroopTrainingBarracksAutomaticResourceSelection);
        Assert.Equal(deadline, item.NextAttemptAt);
        Assert.Equal("(1|2)", item.Payload[BotOptionPayloadKeys.TargetVillageKey]);
        TroopTrainingExecutionSettings.VerifyBeforeSubmit();
    }

    [Fact]
    public async Task ChangedSelectionDuringPreparation_DefersBeforeSubmitAndNextExecutionUsesNewChoice()
    {
        var saved = Settings("Swordsman");
        var item = new QueueItem { TaskName = "build_troops", Payload = saved.ToDictionary() };
        using (TroopTrainingExecutionSettings.BeginScope(() => saved, _ => { }))
        {
            Assert.Equal("Swordsman", QueueExecutionOptionsResolver.Resolve(new BotOptions(), item).TroopTrainingBarracksTroopType);
            await Task.Yield(); // Model asynchronous page preparation and click pacing.
            saved = Settings("Phalanx");
            var wait = Assert.Throws<TaskWaitException>(TroopTrainingExecutionSettings.VerifyBeforeSubmit);
            Assert.Equal("training_settings_changed", wait.ReasonCode);
        }
        using (TroopTrainingExecutionSettings.BeginScope(() => saved, _ => { }))
        {
            Assert.Equal("Phalanx", QueueExecutionOptionsResolver.Resolve(new BotOptions(), item).TroopTrainingBarracksTroopType);
            TroopTrainingExecutionSettings.VerifyBeforeSubmit();
        }
    }

    [Fact]
    public void Scope_IsolatesVillagesAndPreservesLegacyPayloadWhenNoOverrideExists()
    {
        var item = new QueueItem { TaskName = "build_troops", Payload = Settings("Swordsman").ToDictionary() };
        using (TroopTrainingExecutionSettings.BeginScope(() => Settings("Phalanx"), _ => { }))
        {
            using (TroopTrainingExecutionSettings.BeginScope(() => null, _ => { }))
                Assert.Equal("Swordsman", QueueExecutionOptionsResolver.Resolve(new BotOptions(), item).TroopTrainingBarracksTroopType);
            Assert.Equal("Phalanx", QueueExecutionOptionsResolver.Resolve(new BotOptions(), item).TroopTrainingBarracksTroopType);
        }
        Assert.Equal("Swordsman", QueueExecutionOptionsResolver.Resolve(new BotOptions(), item).TroopTrainingBarracksTroopType);
    }

    [Fact]
    public void Resolve_RefreshesBarracksStableAndWorkshopSelectionsTogether()
    {
        var queued = Settings("Swordsman") with
        {
            Stable = Settings("Swordsman").Stable with { Enabled = true, TroopType = "Pathfinder" },
            Workshop = Settings("Swordsman").Workshop with { Enabled = true, TroopType = "Ram" },
        };
        var saved = Settings("Phalanx") with
        {
            Stable = Settings("Phalanx").Stable with { Enabled = true, TroopType = "Theutates Thunder" },
            Workshop = Settings("Phalanx").Workshop with { Enabled = true, TroopType = "Trebuchet" },
        };
        var item = new QueueItem { TaskName = "build_troops", Payload = queued.ToDictionary() };

        using var scope = TroopTrainingExecutionSettings.BeginScope(() => saved, _ => { });
        var resolved = QueueExecutionOptionsResolver.Resolve(new BotOptions(), item);

        Assert.Equal("Phalanx", resolved.TroopTrainingBarracksTroopType);
        Assert.Equal("Theutates Thunder", resolved.TroopTrainingStableTroopType);
        Assert.Equal("Trebuchet", resolved.TroopTrainingWorkshopTroopType);
    }

    private static TroopTrainingPayload Settings(string troop)
    {
        var building = new TroopTrainingBuildingPayload(true, troop, "no_limit", "maximum", 0,
            "resource_percent", 20, 90, 30, 120, true, true, true, false)
        {
            AutomaticResourceSelection = true,
        };
        return new TroopTrainingPayload(building, building with { Enabled = false }, building with { Enabled = false }, 30);
    }
}
