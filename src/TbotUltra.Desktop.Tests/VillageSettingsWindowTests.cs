using Xunit;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Views;

namespace TbotUltra.Desktop.Tests;

// Runs on the shared WPF smoke thread: once any test creates Application.Current, constructing a
// Panel tests run on the shared WPF smoke thread so compiled XAML and templates use one dispatcher.
[Collection(WpfSmokeCollection.Name)]
public sealed class VillageSettingsPanelTests
{
    private readonly WpfSmokeFixture _wpf;

    public VillageSettingsPanelTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void Constructor_LoadsCompiledXamlWithNoVillages()
    {
        _wpf.Run(() =>
        {
            var panel = new VillageSettingsPanel([]);
            Assert.NotNull(panel);
        });
    }

    [Fact]
    public void CheckAllHeaderButtons_ToggleStaticAndEligibleGroupColumns()
    {
        _wpf.Run(() =>
        {
            var rows = new[]
            {
                BuildRow("First", isAutomationEnabled: true, isNpcTradeEnabled: false, isFarmingEnabled: false, canToggleFarming: true),
                BuildRow("Second", isAutomationEnabled: true, isNpcTradeEnabled: true, isFarmingEnabled: true, canToggleFarming: false),
            };
            var panel = new VillageSettingsPanel(rows);
            var grid = Assert.IsType<DataGrid>(panel.FindName("VillageSettingsDataGrid"));
                var checkAllRow = Assert.IsType<VillageSettingsRow>(grid.Items[0]);
                Assert.True(checkAllRow.IsCheckAllRow);

                ClickCheckAll(grid.Columns[0], checkAllRow);
                Assert.All(rows, row => Assert.False(row.IsEnabledForAutomation));

                ClickCheckAll(grid.Columns[0], checkAllRow);
                Assert.All(rows, row => Assert.True(row.IsEnabledForAutomation));

                var npcColumn = grid.Columns
                    .OfType<DataGridTemplateColumn>()
                    .Single(column => HeaderTitle(column) == "NPC");
                ClickCheckAll(npcColumn, checkAllRow);
                Assert.All(rows, row => Assert.True(row.NpcTrade));

                ClickCheckAll(npcColumn, checkAllRow);
                Assert.All(rows, row => Assert.False(row.NpcTrade));
        });
    }

    [Fact]
    public void Constructor_DoesNotShowDemolishGroupColumn()
    {
        _wpf.Run(() =>
        {
            var row = BuildRow("First", true, false, false, true, includeDemolish: true);

            var panel = new VillageSettingsPanel([row]);
            var grid = Assert.IsType<DataGrid>(panel.FindName("VillageSettingsDataGrid"));

            Assert.DoesNotContain(grid.Columns, column => HeaderTitle(column) == "Demolish");
        });
    }

    [Fact]
    public void Changes_ArePersistedImmediatelyWithoutSaveButton()
    {
        _wpf.Run(() =>
        {
            var row = BuildRow("First", true, false, false, true);
            var enabledChanges = 0;
            var npcChanges = 0;
            var groupChanges = 0;
            var savedNotifications = 0;
            var panel = new VillageSettingsPanel(
                [row],
                onEnabledChanged: _ => enabledChanges++,
                onNpcTradeChanged: _ => npcChanges++,
                onGroupsChanged: _ => groupChanges++,
                onSaved: () => savedNotifications++);

            row.IsEnabledForAutomation = false;
            row.NpcTrade = true;
            row.GroupToggles[0].IsEnabled = true;

            Assert.Equal(1, enabledChanges);
            Assert.Equal(1, npcChanges);
            Assert.Equal(1, groupChanges);
            Assert.Equal(3, savedNotifications);
            Assert.Null(panel.FindName("SettingsFooter"));
        });
    }

    [Fact]
    public void ProtectionColumns_FollowTownHallAndPublishChanges()
    {
        _wpf.Run(() =>
        {
            var row = new VillageSettingsRow
            {
                Name = "BRO",
                AttackScanEnabled = true,
                TroopEvadeEnabled = false,
                GroupToggles =
                [
                    new VillageGroupToggle
                    {
                        GroupKey = "town_hall_celebration",
                        Title = "Town Hall",
                        CanToggle = true,
                    },
                    new VillageGroupToggle
                    {
                        GroupKey = "resource_transfer",
                        Title = "Resource Transfer",
                        CanToggle = true,
                    },
                ],
            };
            var attackScanChanges = 0;
            var troopEvadeChanges = 0;
            var panel = new VillageSettingsPanel(
                [row],
                onAttackScanChanged: _ => attackScanChanges++,
                onTroopEvadeChanged: _ => troopEvadeChanges++);
            var grid = Assert.IsType<DataGrid>(panel.FindName("VillageSettingsDataGrid"));
            var headers = grid.Columns.OrderBy(column => column.DisplayIndex).Select(HeaderTitle).ToList();
            var townHallIndex = headers.IndexOf("Town Hall");

            Assert.Equal("Attack scan", headers[townHallIndex + 1]);
            Assert.Equal("Troop evade", headers[townHallIndex + 2]);

            row.AttackScanEnabled = false;
            row.TroopEvadeEnabled = true;

            Assert.Equal(1, attackScanChanges);
            Assert.Equal(1, troopEvadeChanges);
        });
    }

    [Fact]
    public void CheckAll_PersistsEveryChangedRowAndPublishesOnce()
    {
        _wpf.Run(() =>
        {
            var rows = new[]
            {
                BuildRow("First", true, false, false, true),
                BuildRow("Second", true, false, false, true),
            };
            var enabledChanges = 0;
            var savedNotifications = 0;
            var panel = new VillageSettingsPanel(
                rows,
                onEnabledChanged: _ => enabledChanges++,
                onSaved: () => savedNotifications++);
            var grid = Assert.IsType<DataGrid>(panel.FindName("VillageSettingsDataGrid"));
            var checkAllRow = Assert.IsType<VillageSettingsRow>(grid.Items[0]);

            ClickCheckAll(grid.Columns[0], checkAllRow);

            Assert.Equal(2, enabledChanges);
            Assert.Equal(1, savedNotifications);
        });
    }

    [Fact]
    public void RomanConstructionPriority_IsShownForRomansAndPublishesChanges()
    {
        _wpf.Run(() =>
        {
            var roman = BuildRow("Rome", true, false, false, true, tribe: "Romans", includeConstruction: true);
            var gaul = BuildRow("Gaul", true, false, false, true, tribe: "Gauls", includeConstruction: true);
            var changes = 0;
            var panel = new VillageSettingsPanel(
                [roman, gaul],
                onRomanConstructionPriorityChanged: _ => changes++);
            var grid = Assert.IsType<DataGrid>(panel.FindName("VillageSettingsDataGrid"));

            Assert.Contains(grid.Columns, column => HeaderTitle(column) == "Roman priority");
            Assert.Equal(Visibility.Visible, roman.RomanConstructionPriorityVisibility);
            Assert.Equal(Visibility.Collapsed, gaul.RomanConstructionPriorityVisibility);

            roman.RomanConstructionPriority = RomanConstructionPriority.Resources;

            Assert.Equal(1, changes);
        });
    }

    private static VillageSettingsRow BuildRow(
        string name,
        bool isAutomationEnabled,
        bool isNpcTradeEnabled,
        bool isFarmingEnabled,
        bool canToggleFarming,
        bool includeDemolish = false,
        string tribe = "",
        bool includeConstruction = false) => new()
    {
        Name = name,
        PopText = "100",
        TribeText = tribe,
        IsEnabledForAutomation = isAutomationEnabled,
        NpcTrade = isNpcTradeEnabled,
        GroupToggles = BuildGroupToggles(isFarmingEnabled, canToggleFarming, includeDemolish, includeConstruction),
    };

    private static IReadOnlyList<VillageGroupToggle> BuildGroupToggles(
        bool isFarmingEnabled,
        bool canToggleFarming,
        bool includeDemolish,
        bool includeConstruction)
    {
        var toggles = new List<VillageGroupToggle>
        {
            new()
            {
                GroupKey = "farming",
                Title = "Farming",
                IsEnabled = isFarmingEnabled,
                CanToggle = canToggleFarming,
            },
        };
        if (includeConstruction)
        {
            toggles.Add(new VillageGroupToggle
            {
                GroupKey = "construction",
                Title = "Construction",
                IsEnabled = true,
                CanToggle = true,
            });
        }

        if (includeDemolish)
        {
            toggles.Add(new VillageGroupToggle
            {
                GroupKey = "demolish",
                Title = "Demolish",
                IsEnabled = false,
                CanToggle = true,
            });
        }

        return toggles;
    }

    private static void ClickCheckAll(DataGridColumn column, VillageSettingsRow checkAllRow)
    {
        var templateColumn = Assert.IsType<DataGridTemplateColumn>(column);
        var cell = Assert.IsType<Grid>(templateColumn.CellTemplate.LoadContent());
        cell.DataContext = checkAllRow;
        var button = Assert.Single(cell.Children.OfType<Button>());
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    [Fact]
    public void Constructor_DoesNotBuildOverviewBeforeThePanelIsLoaded()
    {
        _wpf.Run(() =>
        {
            var calls = 0;
            var panel = new VillageSettingsPanel(
                [],
                section: "Overview",
                overviewProjectionProvider: _ =>
                {
                    calls++;
                    return Task.FromResult<VillageOverviewProjection>(null!);
                },
                overviewSourceVersionProvider: () => 1);

            Assert.Equal(0, calls);
            Assert.Equal("Loading overview...", Assert.IsType<TextBlock>(panel.FindName("OverviewUpdatedTextBlock")).Text);
        });
    }

    private static string HeaderTitle(DataGridColumn column)
    {
        return (column.Header as TextBlock)?.Text ?? string.Empty;
    }
}
