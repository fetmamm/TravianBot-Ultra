using System.Diagnostics;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AutomationQueueItemFailureTests
{
    [Fact]
    public async Task TypedWait_DefersWithoutConsumingExecutionRetry()
    {
        var port = new InMemoryPort();
        var item = Item("collect_tasks");

        var shouldContinue = await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new TaskWaitException(45, "queued work"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.True(shouldContinue);
        Assert.Equal(TimeSpan.FromSeconds(45), port.DeferredDelay);
        Assert.Equal(["defer", "farm-refresh", "refresh-indicators"], port.Trace);
        Assert.DoesNotContain("failed", port.Trace);
    }

    [Fact]
    public async Task HeroAwayWait_PersistsTypedDeferReason()
    {
        var port = new InMemoryPort();
        var item = Item("hero_manage");

        await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new TaskWaitException(90, "hero away", TaskWaitReasons.HeroAway),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal("away", item.Payload["hero_defer_reason"]);
        Assert.Contains("update-payload", port.Trace);
    }

    [Fact]
    public async Task HeroMissingRallyPoint_QueuesRepairAndDefersHero()
    {
        var port = new InMemoryPort();
        var item = Item("hero_manage");
        var request = new HeroRallyPointRepairRequest(24443, "WHY", 164, 110);

        await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new HeroMissingRallyPointTaskWaitException(request),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal(TimeSpan.FromSeconds(60), port.DeferredDelay);
        Assert.Same(request, port.HeroRallyPointRepairRequest);
        Assert.Contains("queue-hero-rally-point", port.Trace);
    }

    [Fact]
    public async Task UnclassifiedFailure_ConsumesRetryAndRaisesTerminalAlarmWhenNeeded()
    {
        var port = new InMemoryPort();
        var item = Item("collect_tasks");

        var shouldContinue = await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new InvalidOperationException("boom"),
            "[AUTOQ 2]",
            Stopwatch.StartNew(),
            AutomationRunMode.AutoQueue);

        Assert.True(shouldContinue);
        Assert.Equal(["failed", "storage-failed", "alarm"], port.Trace);
        Assert.Contains("boom", port.Logs[^1]);
    }

    [Fact]
    public async Task MissingSmithy_WhenVerificationFindsIt_RetriesWithoutBlockingTroops()
    {
        var port = new InMemoryPort { SmithyMissing = false };

        await new AutomationQueueItemFailure(port).HandleAsync(
            Item("upgrade_troops_at_smithy"),
            new InvalidOperationException("Smithy not found in this village"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal(TimeSpan.FromSeconds(10), port.DeferredDelay);
        Assert.Equal(["verify-smithy", "defer"], port.Trace);
    }

    [Fact]
    public async Task CompletedSmithyWork_DisablesOnlyTheItemsVillage()
    {
        var port = new InMemoryPort { DisableTroopsForVillage = true };

        await new AutomationQueueItemFailure(port).HandleAsync(
            Item("upgrade_troops_at_smithy"),
            new InvalidOperationException("Smithy: All done"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal(["succeeded", "disable-troops-village"], port.Trace);
        Assert.DoesNotContain("block-troops-global", port.Trace);
    }

    [Fact]
    public async Task MissingTownHall_DisablesCelebrationsForTheItemsVillage()
    {
        var port = new InMemoryPort();

        await new AutomationQueueItemFailure(port).HandleAsync(
            Item("run_town_hall_celebration"),
            new InvalidOperationException("town_hall_unavailable=missing"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal(["succeeded", "disable-town-hall-village"], port.Trace);
    }

    [Fact]
    public async Task DurationAnomaly_RequestsDorf2Verification()
    {
        var port = new InMemoryPort();

        await new AutomationQueueItemFailure(port).HandleAsync(
            Item("upgrade_resource_to_level"),
            new TaskWaitException(
                1,
                "main_building_duration_anomaly=true queue_wait_seconds=1"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Contains("verify-main-building", port.Trace);
    }

    [Fact]
    public async Task ResourceCropShortage_PersistsThresholdAndStartsRecovery()
    {
        var port = new InMemoryPort();
        var item = Item("upgrade_all_resources_to_level");

        await new AutomationQueueItemFailure(port).HandleAsync(
            item,
            new TaskWaitException(
                1,
                "blocked by crop shortage wait_reason=crop_shortage crop_shortage_required_free_crop=2 queue_wait_seconds=1"),
            "[LOOP 1]",
            Stopwatch.StartNew(),
            AutomationRunMode.ContinuousLoop);

        Assert.Equal(BotOptionPayloadKeys.UpgradeDeferReasonCropShortage, item.Payload[BotOptionPayloadKeys.UpgradeDeferReason]);
        Assert.Equal("2", item.Payload[BotOptionPayloadKeys.CropShortageRequiredFreeCrop]);
        Assert.Equal("0", item.Payload[BotOptionPayloadKeys.CropShortageCompletedSteps]);
        Assert.Contains("crop-defer", port.Trace);
    }

    [Fact]
    public async Task UnknownUpgradeBlock_AlarmsOnlyOncePerPersistedSignature()
    {
        var port = new InMemoryPort();
        var item = Item("upgrade_all_resources_to_level");
        var wait = new TaskWaitException(
            1800,
            "unknown_upgrade_block_signature=abc123 queue_wait_seconds=1800");
        var handler = new AutomationQueueItemFailure(port);

        await handler.HandleAsync(item, wait, "[LOOP 1]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        await handler.HandleAsync(item, wait, "[LOOP 2]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);

        Assert.Equal("abc123", item.Payload[BotOptionPayloadKeys.UnknownUpgradeBlockSignature]);
        Assert.Single(port.Logs, log => log.StartsWith("ALARM: Unknown Travian upgrade block", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedConstructionPageTimerWait_UsesBoundedBackoff()
    {
        var port = new InMemoryPort();
        var item = Item("construct_building");
        var wait = new TaskWaitException(
            60,
            "Building slot 20 (Granary) construct: blocked by resources. "
            + "queue_wait_seconds=60 upgrade_blocked_label=Building_slot_20_(Granary)_construct "
            + "upgrade_wait_reason=page_timer upgrade_wait_seconds=60");
        var handler = new AutomationQueueItemFailure(port, randomInt: (minimum, _) => minimum);

        await handler.HandleAsync(item, wait, "[LOOP 1]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        Assert.Equal(TimeSpan.FromMinutes(1), port.DeferredDelay);

        await handler.HandleAsync(item, wait, "[LOOP 2]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        Assert.Equal(TimeSpan.FromMinutes(2), port.DeferredDelay);

        await handler.HandleAsync(item, wait, "[LOOP 3]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        Assert.Equal(TimeSpan.FromMinutes(5), port.DeferredDelay);
        Assert.Contains(port.Logs, log => log.Contains("repeated identical resource wait", StringComparison.OrdinalIgnoreCase));

        for (var attempt = 4; attempt <= 7; attempt++)
        {
            await handler.HandleAsync(item, wait, $"[LOOP {attempt}]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        }
        Assert.Equal(TimeSpan.FromMinutes(30), port.DeferredDelay);

        var changedBlocker = new TaskWaitException(
            60,
            "Building slot 21 (Warehouse) construct: blocked by resources. "
            + "queue_wait_seconds=60 upgrade_blocked_label=Building_slot_21_(Warehouse)_construct "
            + "upgrade_wait_reason=page_timer upgrade_wait_seconds=60");
        await handler.HandleAsync(item, changedBlocker, "[LOOP 8]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        Assert.Equal(TimeSpan.FromMinutes(1), port.DeferredDelay);

        var authoritativeLongWait = new TaskWaitException(
            3600,
            "Building slot 22 (Granary) construct: blocked by resources. "
            + "queue_wait_seconds=3600 upgrade_blocked_label=Building_slot_22_(Granary)_construct "
            + "upgrade_wait_reason=page_timer upgrade_wait_seconds=3600");
        await handler.HandleAsync(item, authoritativeLongWait, "[LOOP 9]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        Assert.Equal(TimeSpan.FromHours(1), port.DeferredDelay);
    }

    [Fact]
    public async Task DeferredConstructionPageTimer_CannotWakeFromItsOwnEmptyOrUnchangedFullSnapshot()
    {
        var port = new InMemoryPort();
        var item = Item("construct_building");
        var wait = new TaskWaitException(
            60,
            "Building slot 20 (Granary) construct: blocked by resources. "
            + "queue_wait_seconds=60 upgrade_blocked_label=Building_slot_20_(Granary)_construct "
            + "upgrade_wait_reason=page_timer upgrade_wait_seconds=60 "
            + "upgrade_current_wood=800 upgrade_current_clay=800 "
            + "upgrade_current_iron=800 upgrade_current_crop=1200");
        var handler = new AutomationQueueItemFailure(port, randomInt: (minimum, _) => minimum);

        await handler.HandleAsync(item, wait, "[LOOP 1]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);

        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        item.Status = QueueStatus.Pending;
        item.NextAttemptAt = now + Assert.IsType<TimeSpan>(port.DeferredDelay);
        var unchangedFullResources = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 800,
            ["clay"] = 800,
            ["iron"] = 800,
            ["crop"] = 1200,
        };

        Assert.False(ConstructionQueueState.ShouldPrepareConfirmedEmptyQueueHead(
            item,
            now,
            ConstructionStatusObservationOrigin.PostDeferredAttempt));
        Assert.False(ConstructionQueueState.ShouldPrepareConfirmedEmptyQueueHead(
            item,
            now,
            ConstructionStatusObservationOrigin.Independent));
        Assert.False(ConstructionRepeatedWaitBackoffPolicy.ShouldReleaseForChangedFullResourceObservation(
            item.Payload,
            unchangedFullResources));
        Assert.True(item.NextAttemptAt > now);

        var changedFullResources = new Dictionary<string, long>(unchangedFullResources, StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = 801,
        };
        Assert.True(ConstructionRepeatedWaitBackoffPolicy.ShouldReleaseForChangedFullResourceObservation(
            item.Payload,
            changedFullResources));
    }

    [Fact]
    public async Task RepeatedResourceUpgradePageTimer_UsesTheSameBackoffGuard()
    {
        var port = new InMemoryPort();
        var item = Item("upgrade_all_resources_to_level");
        var wait = new TaskWaitException(
            60,
            "Resource slot 1 (Woodcutter) upgrade: blocked by resources. "
            + "queue_wait_seconds=60 upgrade_blocked_label=Resource_slot_1_(Woodcutter)_upgrade "
            + "upgrade_wait_reason=page_timer upgrade_wait_seconds=60");
        var handler = new AutomationQueueItemFailure(port, randomInt: (minimum, _) => minimum);

        await handler.HandleAsync(item, wait, "[LOOP 1]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);
        await handler.HandleAsync(item, wait, "[LOOP 2]", Stopwatch.StartNew(), AutomationRunMode.ContinuousLoop);

        Assert.Equal(TimeSpan.FromMinutes(2), port.DeferredDelay);
        Assert.Equal("2", item.Payload[BotOptionPayloadKeys.ConstructionDeferBackoffCount]);
    }

    private static QueueItem Item(string taskName) => new()
    {
        Id = Guid.NewGuid(),
        TaskName = taskName,
        Status = QueueStatus.Running,
        Payload = [],
    };

    private sealed class InMemoryPort : IAutomationQueueItemFailurePort
    {
        public List<string> Trace { get; } = [];
        public List<string> Logs { get; } = [];
        public TimeSpan? DeferredDelay { get; private set; }
        public bool? SmithyMissing { get; init; }
        public bool DisableTroopsForVillage { get; init; }
        public HeroRallyPointRepairRequest? HeroRallyPointRepairRequest { get; private set; }
        public ValueTask<bool?> VerifySmithyMissingAsync(QueueItem item)
        {
            Trace.Add("verify-smithy");
            return ValueTask.FromResult(SmithyMissing);
        }
        public bool MarkSucceeded(Guid itemId)
        {
            Trace.Add("succeeded");
            return true;
        }
        public bool DisableTroopsGroupForVillage(QueueItem item, out string blockedVillageName)
        {
            Trace.Add("disable-troops-village");
            blockedVillageName = "Alpha";
            return DisableTroopsForVillage;
        }
        public void SetTroopsBlockedState(string reasonKey, string reasonText) =>
            Trace.Add("block-troops-global");
        public void DisableTownHallForVillage(string villageKey, string? villageName) =>
            Trace.Add("disable-town-hall-village");
        public ValueTask ApplyConstructionInlineWaitAsync(
            TimeSpan delay,
            string? humanizeVillageKey,
            TimeSpan? humanizeWait) => ValueTask.CompletedTask;
        public ValueTask ApplyHeroLowHpCooldownAsync(TimeSpan delay) => ValueTask.CompletedTask;
        public Guid EnsureHeroRallyPointRepairQueued(QueueItem item, HeroRallyPointRepairRequest request)
        {
            HeroRallyPointRepairRequest = request;
            Trace.Add("queue-hero-rally-point");
            return Guid.NewGuid();
        }
        public void ApplyBreweryCelebrationDeferSignal(string? message, TimeSpan delay) { }
        public void ApplyTownHallCelebrationDeferSignal(QueueItem item, string? message, TimeSpan delay) { }
        public bool MarkDeferred(Guid itemId, TimeSpan delay)
        {
            DeferredDelay = delay;
            Trace.Add("defer");
            return true;
        }
        public string? GetVillageKey(QueueItem item) => "1:2";
        public string? GetVillageName(QueueItem item) => "Alpha";
        public void ClearConstructionLoginFillForBlockedHead(QueueItem item, string source) =>
            Trace.Add("clear-fill");
        public AutomationConstructionRequirementContext GetConstructionRequirementContext(QueueItem item) =>
            new(null, []);
        public bool PatchDeferredPayload(QueueItem item, Dictionary<string, string> payload)
        {
            Trace.Add("patch");
            return true;
        }
        public bool MarkPermanentlyFailed(Guid itemId)
        {
            Trace.Add("permanent-failure");
            return true;
        }
        public void RaisePermanentFailureAlarm(QueueItem item, string message) => Trace.Add("alarm");
        public ValueTask RefreshVillageActivityIndicatorsAsync()
        {
            Trace.Add("refresh-indicators");
            return ValueTask.CompletedTask;
        }
        public string FormatServerTime(DateTimeOffset value) => value.ToString("O");
        public void RebindPendingTemplateStep(QueueItem item, int effectiveSlotId) => Trace.Add("rebind-template");
        public ValueTask HandleStorageCapacityDependencyAsync(
            QueueItem item,
            Dictionary<string, string> payload) => ValueTask.CompletedTask;
        public ValueTask RefreshFarmListsAfterAutoSendAsync(QueueItem item, string message)
        {
            Trace.Add("farm-refresh");
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshConstructionStatusAfterDeferAsync() => ValueTask.CompletedTask;
        public ValueTask VerifyMainBuildingAfterDurationAnomalyAsync(QueueItem item)
        {
            Trace.Add("verify-main-building");
            return ValueTask.CompletedTask;
        }
        public ValueTask HandleCropShortageDeferAsync(QueueItem item)
        {
            Trace.Add("crop-defer");
            return ValueTask.CompletedTask;
        }
        public ValueTask RefreshTroopTrainingAfterBuildAsync(QueueItem item) => ValueTask.CompletedTask;
        public bool UpdateDeferredPayload(Guid itemId, Dictionary<string, string> payload)
        {
            Trace.Add("update-payload");
            return true;
        }
        public bool MarkExecutionFailed(Guid itemId)
        {
            Trace.Add("failed");
            return true;
        }
        public void HandleStorageDependencyFailed(QueueItem item, string message) =>
            Trace.Add("storage-failed");
        public string FormatException(Exception exception) => exception.Message;
        public void Log(string message) => Logs.Add(message);
    }
}
