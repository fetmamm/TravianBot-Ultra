using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class IncomingAttackFlowSourceTests
{
    [Fact]
    public void RallyPointRead_OpensIncomingOnlyUrlAndWaitsForFilterControl()
    {
        var source = Read("TbotUltra.Worker", "Services", "Automation", "Combat", "TravianClient.IncomingAttacks.cs");

        var constructionCheck = source.IndexOf("ReadRallyPointConstructionStateAsync", StringComparison.Ordinal);
        var rallyPointOpen = source.IndexOf("/build.php?gid=16&tt=1&filter=1&subfilters=1", StringComparison.Ordinal);
        Assert.True(constructionCheck >= 0);
        Assert.True(rallyPointOpen > constructionCheck);
        Assert.Contains("/build.php?gid=16&tt=1&filter=1&subfilters=1", source, StringComparison.Ordinal);
        Assert.Contains("await category.WaitForAsync", source, StringComparison.Ordinal);
        Assert.Contains("img.filterCategory1", source, StringComparison.Ordinal);
        Assert.Contains("img.subFilterCategory1", source, StringComparison.Ordinal);
        Assert.Contains("img.subFilterCategory2", source, StringComparison.Ordinal);
        Assert.Contains("img.subFilterCategory3", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeEmptyDorf1Read_ClearsPendingAndConfirmedVillageState()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        Assert.Contains("resolvedSignals.All(signal => !string.Equals(signal.Key, activeKey", source, StringComparison.Ordinal);
        Assert.Contains("ClearIncomingAttacksAfterAuthoritativeDorf1Read(activeKey", source, StringComparison.Ordinal);
        Assert.Contains("_incomingAttackPendingSignals.Remove(villageKey)", source, StringComparison.Ordinal);
        Assert.Contains("_incomingAttacksByVillage.Remove(villageKey)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("knownAttacks.Count == 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TroopEvasion_ChecksDorf1TroopPresenceBeforeOpeningRallyPoint()
    {
        var source = Read("TbotUltra.Worker", "Services", "Automation", "Combat", "TravianClient.TroopEvasion.cs");
        var presenceCheck = source.IndexOf("ReadTroopPresenceOnCurrentDorf1Async", StringComparison.Ordinal);
        var rallyPointOpen = source.IndexOf("EnsureRallyPointAndOpenSendTroopsPageAsync", StringComparison.Ordinal);

        Assert.True(presenceCheck >= 0);
        Assert.True(rallyPointOpen > presenceCheck);
        Assert.Contains("TroopEvasionOutcome.NoTroops", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TroopEvasion_CachedNoTroopsCanSkipOnlyInitialLeadMilestone()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.TroopEvasion.cs");

        Assert.Contains("due.Milestone == \"lead\"", source, StringComparison.Ordinal);
        Assert.Contains("cachedStatus.HasTroopsAtHome == false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingAttackRows_AreCreatedWithAVisibleCountdown()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");

        Assert.Contains("CountdownText = FormatIncomingAttackCountdown(attack.ArrivalAtUtc", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingAttackSoundSettings_HaveExplanatoryInfoTooltips()
    {
        var xaml = Read("TbotUltra.Desktop", "Views", "ReinforcementsPanel.xaml");

        Assert.Contains("x:Name=\"IncomingAttackSoundInfoIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Turning this off does not disable attack monitoring.", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IncomingAttackSoundCooldownInfoIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("New attacks are still recorded during the cooldown", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingAttackUi_PreservesAllVillagesAndGroupsTheirRows()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        var xaml = Read("TbotUltra.Desktop", "Views", "ReinforcementsPanel.xaml");

        Assert.Contains("SelectMany(pair => pair.Value", source, StringComparison.Ordinal);
        Assert.Contains("PropertyGroupDescription(nameof(IncomingAttackRowItem.TargetVillageName))", source, StringComparison.Ordinal);
        Assert.Contains("SortDescription(nameof(IncomingAttackRowItem.ArrivalAtUtc)", source, StringComparison.Ordinal);
        Assert.Contains("<DataGrid.GroupStyle>", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ItemCount}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrivedAttack_DoesNotCreateAnotherPendingWarning()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        var start = source.IndexOf("private void TickIncomingAttacks", StringComparison.Ordinal);
        var end = source.IndexOf("private void RefreshIncomingAttackUi", start, StringComparison.Ordinal);
        var tick = source[start..end];

        Assert.Contains("ShouldKeepPendingSignal", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("new IncomingAttackSignal", tick, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedDorf1Signals_DoNotCauseMinuteByMinuteRallyPointReads()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");

        Assert.Contains("IncomingAttackObservationPolicy.ShouldReadDetails", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromMinutes(1)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredFallbackTimer_PreservesRetryDeadline()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        var start = source.IndexOf("foreach (var key in expiredPendingKeys)", StringComparison.Ordinal);
        var end = source.IndexOf("if (expiredKeys.Count", start, StringComparison.Ordinal);
        var expiryCleanup = source[start..end];

        Assert.Contains("_incomingAttackPendingSignals.Remove(key)", expiryCleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("_incomingAttackLastReadUtc.Remove(key)", expiryCleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledVillage_GatesIncomingReadsAndTroopEvasion()
    {
        var incomingSource = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        var evasionSource = Read("TbotUltra.Desktop", "MainWindow.TroopEvasion.cs");

        Assert.Contains("if (!IsIncomingAttackMonitoringEnabled(villageKey))", incomingSource, StringComparison.Ordinal);
        Assert.Contains("discarded result for disabled village", incomingSource, StringComparison.Ordinal);
        Assert.Contains("Where(pair => IsIncomingAttackMonitoringEnabled(pair.Key))", evasionSource, StringComparison.Ordinal);
        Assert.Contains("Incoming monitoring disabled", evasionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingVillageMonitoring_PreservesConfirmedAttackRows()
    {
        var source = Read("TbotUltra.Desktop", "MainWindow.IncomingAttacks.cs");
        var start = source.IndexOf("internal void OnIncomingAttackMonitoringChanged", StringComparison.Ordinal);
        var handler = source[start..];

        Assert.DoesNotContain("ClearIncomingAttacksAfterAuthoritativeDorf1Read", handler, StringComparison.Ordinal);
        Assert.Contains("CancelTroopEvasionForClearedVillage", handler, StringComparison.Ordinal);

        var restoreStart = source.IndexOf("foreach (var attack in state.Attacks)", StringComparison.Ordinal);
        var restoreEnd = source.IndexOf("_incomingAttackPendingSignals.Clear()", restoreStart, StringComparison.Ordinal);
        var confirmedAttackRestore = source[restoreStart..restoreEnd];
        Assert.DoesNotContain("IsIncomingAttackMonitoringEnabled", confirmedAttackRestore, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidEvasionEnable_UsesThemedWarningWithSoftGreenOk()
    {
        var panelSource = Read("TbotUltra.Desktop", "Views", "TroopEvasionPanel.xaml.cs");
        var mainSource = Read("TbotUltra.Desktop", "MainWindow.TroopEvasion.cs");

        Assert.Contains("EnableValidationRequested?.Invoke(village)", panelSource, StringComparison.Ordinal);
        Assert.Contains("AppDialog.ShowCustom", panelSource, StringComparison.Ordinal);
        Assert.Contains("successResult: MessageBoxResult.OK", panelSource, StringComparison.Ordinal);
        Assert.Contains("Complete the following:", mainSource, StringComparison.Ordinal);
        Assert.Contains("Enter valid target X and Y coordinates", mainSource, StringComparison.Ordinal);
        Assert.Contains("Select at least one troop or Hero", mainSource, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
    {
        var root = ProjectRootLocator.FindProjectRoot();
        return File.ReadAllText(Path.Combine([root, "src", .. parts]));
    }
}
