using TbotUltra.Desktop.Models;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LiveUiModelUpdateTests
{
    [Fact]
    public void BuildQueueRow_ApplySnapshot_NotifiesOnlyChangedCountdown()
    {
        var row = new TravianBuildQueueRow
        {
            Name = "Warehouse",
            LevelText = "Level 5",
            CountdownText = "01:00",
            FinishAtText = "12:00:00",
        };
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ApplySnapshot(new TravianBuildQueueRow
        {
            Name = "Warehouse",
            LevelText = "Level 5",
            CountdownText = "00:59",
            FinishAtText = "12:00:00",
        });

        Assert.Equal([nameof(TravianBuildQueueRow.CountdownText)], changed);
    }

    [Fact]
    public void VillageSelectionItem_EqualSlots_DoNotNotifyAgain()
    {
        var item = new VillageSelectionItem();
        var slot = new VillageActivitySlot
        {
            IsActive = true,
            Label = "B",
            Tooltip = "Barracks: training (00:10)",
        };
        var notifications = 0;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VillageSelectionItem.TroopSlots))
            {
                notifications++;
            }
        };

        item.TroopSlots = [slot];
        item.TroopSlots = [slot with { }];

        Assert.Equal(1, notifications);
    }
}
