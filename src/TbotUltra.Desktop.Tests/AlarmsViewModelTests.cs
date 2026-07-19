using System;
using System.Linq;
using TbotUltra.Desktop.ViewModels;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AlarmsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordAlarm_NewEntryInsertsFirstAndCounts()
    {
        var vm = new AlarmsViewModel();

        var isNew = vm.RecordAlarm("[12:00:00] ALARM one", "acc|one", isAcknowledgedAlarm: false, Now);

        Assert.True(isNew);
        var entry = Assert.Single(vm.Entries);
        Assert.Equal("[12:00:00] ALARM one", entry.Text);
        Assert.False(entry.IsAcknowledged);
        Assert.Equal(1, vm.UnacknowledgedCount);
    }

    [Fact]
    public void RecordAlarm_DuplicateWithinWindowCoalescesWithoutNewRow()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now);

        var isNew = vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now.AddMinutes(10));

        Assert.False(isNew);
        var entry = Assert.Single(vm.Entries);
        Assert.Equal(2, entry.OccurrenceCount);
        Assert.Contains("(x2, first", entry.Text);
        Assert.Equal(1, vm.UnacknowledgedCount);
    }

    [Fact]
    public void RecordAlarm_DuplicateOutsideWindowAddsNewRow()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now);

        var isNew = vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now.AddMinutes(31));

        Assert.True(isNew);
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal(2, vm.UnacknowledgedCount);
    }

    [Fact]
    public void RecordAlarm_ReoccurrenceReraisesAcknowledgedEntry()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now);
        vm.AcknowledgeAll();
        Assert.Equal(0, vm.UnacknowledgedCount);

        var isNew = vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now.AddMinutes(5));

        Assert.False(isNew);
        Assert.False(vm.Entries[0].IsAcknowledged);
        Assert.Equal(1, vm.UnacknowledgedCount);
    }

    [Fact]
    public void RecordAlarm_AutoAcknowledgedEntryDoesNotCount()
    {
        var vm = new AlarmsViewModel();

        var isNew = vm.RecordAlarm("ALARM auto", "acc|auto", isAcknowledgedAlarm: true, Now);

        Assert.True(isNew);
        Assert.True(vm.Entries[0].IsAcknowledged);
        Assert.Equal(0, vm.UnacknowledgedCount);
    }

    [Fact]
    public void RecordAlarm_AcknowledgedReoccurrenceOfAcknowledgedEntryStaysAcknowledged()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM auto", "acc|auto", isAcknowledgedAlarm: true, Now);

        vm.RecordAlarm("ALARM auto", "acc|auto", isAcknowledgedAlarm: true, Now.AddMinutes(1));

        Assert.True(vm.Entries[0].IsAcknowledged);
        Assert.Equal(0, vm.UnacknowledgedCount);
    }

    [Fact]
    public void AcknowledgeWhere_OnlyMatchingEntriesRecomputesCount()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM language", "acc|lang", isAcknowledgedAlarm: false, Now);
        vm.RecordAlarm("ALARM other", "acc|other", isAcknowledgedAlarm: false, Now);

        var changed = vm.AcknowledgeWhere(entry => entry.Text.Contains("language"));

        Assert.True(changed);
        Assert.Equal(1, vm.UnacknowledgedCount);
        Assert.True(vm.Entries.Single(e => e.Text.Contains("language")).IsAcknowledged);
    }

    [Fact]
    public void AcknowledgeWhere_NoMatchReturnsFalse()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM other", "acc|other", isAcknowledgedAlarm: false, Now);

        Assert.False(vm.AcknowledgeWhere(entry => entry.Text.Contains("language")));
        Assert.Equal(1, vm.UnacknowledgedCount);
    }

    [Fact]
    public void Clear_EmptiesEntriesAndCounter()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM one", "acc|one", isAcknowledgedAlarm: false, Now);

        vm.Clear();

        Assert.Empty(vm.Entries);
        Assert.Equal(0, vm.UnacknowledgedCount);
    }

    [Fact]
    public void DifferentSignaturesNeverCoalesce()
    {
        var vm = new AlarmsViewModel();
        vm.RecordAlarm("ALARM one", "accA|one", isAcknowledgedAlarm: false, Now);

        var isNew = vm.RecordAlarm("ALARM one", "accB|one", isAcknowledgedAlarm: false, Now.AddMinutes(1));

        Assert.True(isNew);
        Assert.Equal(2, vm.Entries.Count);
    }
}
