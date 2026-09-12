using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Desktop.Views;
using TbotUltra.Worker.Domain;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Constructs every tab panel with the App theme dictionaries loaded. These catch the class of bug a
/// unit test cannot: a StaticResource key that does not exist, a style key renamed on one side only,
/// or a XAML parse error. All of those build cleanly and only blow up when the panel is first shown.
/// </summary>
[Collection(WpfSmokeCollection.Name)]
public sealed class PanelSmokeTests
{
    private readonly WpfSmokeFixture _wpf;

    public PanelSmokeTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    public static TheoryData<string, Func<UserControl>> Panels() => new()
    {
        { nameof(DashboardPanel), () => new DashboardPanel() },
        { nameof(DashboardHubPanel), () => new DashboardHubPanel() },
        { nameof(BuildingsPanel), () => new BuildingsPanel() },
        { nameof(ResourcesPanel), () => new ResourcesPanel() },
        { nameof(ResourcesHubPanel), () => new ResourcesHubPanel() },
        { nameof(HeroPanel), () => new HeroPanel() },
        { nameof(HeroHubPanel), () => new HeroHubPanel() },
        { nameof(TroopsPanel), () => new TroopsPanel() },
        { nameof(TroopsHubPanel), () => new TroopsHubPanel() },
        { nameof(FarmingPanel), () => new FarmingPanel() },
        { nameof(QueuePanel), () => new QueuePanel() },
        { nameof(LogsPanel), () => new LogsPanel() },
        { nameof(InboxPanel), () => new InboxPanel() },
        { nameof(NpcTradePanel), () => new NpcTradePanel() },
        { nameof(NpcTradeHubPanel), () => new NpcTradeHubPanel() },
        { nameof(ReinforcementsPanel), () => new ReinforcementsPanel() },
        { nameof(BusyOverlayControl), () => new BusyOverlayControl() },
        { nameof(StoragePreflightPlanView), () => new StoragePreflightPlanView("Preflight", []) },
        { nameof(UpdateConfirmationView), () => new UpdateConfirmationView("1.2.3") },
        { nameof(LobbyWorldSelectionView), () => new LobbyWorldSelectionView(new(
            "Configured world",
            "https://wrong.x1.europe.travian.com",
            [new("12345678-1234-1234-1234-123456789012", "Europe 3", "player · Europe 3 · PLAY NOW")])) },
    };

    [Theory]
    [MemberData(nameof(Panels))]
    public void Panel_LoadsWithoutXamlOrResourceErrors(string name, Func<UserControl> create)
    {
        _wpf.Run(() =>
        {
            var panel = create();
            Assert.NotNull(panel);

            // Measure/arrange so templates expand and lazily-applied styles actually run, instead of
            // only proving the constructor parsed the XAML.
            panel.Measure(new Size(1280, 900));
            panel.Arrange(new Rect(0, 0, 1280, 900));
            panel.UpdateLayout();
        });

        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void FarmingPanel_ContainsLossMoveControls()
    {
        _wpf.Run(() =>
        {
            var panel = new FarmingPanel();

            Assert.Equal("Red attacks", panel.DeactivateRedLossesOption.Content);
            Assert.Equal("Yellow attacks", panel.DeactivateYellowLossesOption.Content);
            Assert.Equal("Move to list", panel.MoveRedLossesOption.Content);
            Assert.Equal("Move to list", panel.MoveYellowLossesOption.Content);
            Assert.Equal("Red", panel.DeactivateRedOasisLossesOption.Content);
            Assert.Equal("Yellow", panel.DeactivateYellowOasisLossesOption.Content);
            Assert.Equal("DisplayText", panel.RedLossDestinationOption.DisplayMemberPath);
            Assert.Equal("DisplayText", panel.YellowLossDestinationOption.DisplayMemberPath);
        });
    }

    [Fact]
    public void DashboardHubPanel_ContainsRequestedTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new DashboardHubPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("DashboardTabControl"));

            Assert.Equal(
                new[] { "Dashboard", "Village settings", "Village overview" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void DashboardPanel_RendersIncomingAttackPulseWithoutAnimatingFrozenTransform()
    {
        _wpf.Run(() =>
        {
            var panel = new DashboardPanel();
            var villages = Assert.IsType<ItemsControl>(panel.FindName("DashboardVillageList"));
            villages.ItemsSource = new[]
            {
                new VillageSelectionItem
                {
                    Name = "BRE",
                    Url = "dorf1.php?newdid=1",
                    CoordX = 25,
                    CoordY = -197,
                    HasIncomingAttack = true,
                },
            };

            panel.Measure(new Size(1280, 900));
            panel.Arrange(new Rect(0, 0, 1280, 900));
            panel.UpdateLayout();
        });
    }

    [Fact]
    public void FarmingPanel_ContainsFarmingAndInactiveOasisScanTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new FarmingPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("FarmingTabControl"));
            var travcoHost = Assert.IsType<ContentControl>(panel.FindName("TravcoToolsHost"));
            Assert.IsType<RadioButton>(panel.FindName("FarmSendSharedScheduleRadioButton"));

            Assert.Equal(
                new[] { "Farming", "Inactive / oasis scan" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
            Assert.Null(travcoHost.Content);
            Assert.Null(panel.FindName("TravcoInactiveSearchButton"));
        });
    }

    [Fact]
    public void TravcoToolsControl_RendersCompleteInlineWorkspace()
    {
        _wpf.Run(() =>
        {
            var root = Path.GetTempPath();
            var control = new TravcoToolsControl(
                new TravcoListStore(root, () => "smoke-account"),
                new AllVillagesImportSettingsStore(root, () => "smoke-account", () => "https://example.invalid"),
                [new VillageSelectionItem { Name = "Capital", CoordX = 10, CoordY = -20, IsCapital = true }],
                log: null);

            control.Measure(new Size(1280, 800));
            control.Arrange(new Rect(0, 0, 1280, 800));
            control.UpdateLayout();

            Assert.IsType<Button>(control.FindName("AddAllVillagesButton"));
            Assert.IsType<Button>(control.FindName("InactiveSearchButton"));
            Assert.IsType<Button>(control.FindName("AnalyzeMapOasisButton"));
            Assert.Equal(0, Grid.GetColumn(Assert.IsType<Border>(control.FindName("TravcoCard"))));
            Assert.Equal(2, Grid.GetColumn(Assert.IsType<Border>(control.FindName("OasisCard"))));
            Assert.Equal(4, Grid.GetColumn(Assert.IsType<Border>(control.FindName("MapSqlCard"))));
            var workspaceInfo = Assert.IsType<ContentControl>(control.FindName("WorkspaceInfoIcon"));
            Assert.Equal(
                "Collect targets, review saved lists and calculate travel distance in one workspace.",
                workspaceInfo.ToolTip);
            Assert.IsType<DataGrid>(control.FindName("SavedListsListBox"));
            Assert.IsType<DataGrid>(control.FindName("ResultsDataGrid"));
            var finishButton = Assert.IsType<Button>(control.FindName("CloseTravcoTabButton"));
            var finishAttention = Assert.IsType<Border>(control.FindName("CloseTravcoTabAttentionBorder"));
            Assert.False(finishButton.IsEnabled);
            Assert.Equal(Visibility.Collapsed, finishAttention.Visibility);

            control.SetSessionState(active: true, browserTabOpen: false);
            Assert.True(finishButton.IsEnabled);
            Assert.Equal("Finish session", finishButton.Content);
            Assert.Equal(Visibility.Visible, finishAttention.Visibility);

            control.SetSessionState(active: true, browserTabOpen: true);
            Assert.Equal("Close Travco tab", finishButton.Content);
        });
    }

    [Fact]
    public void ReinforcementsPanel_ContainsReinforcementsAndIncomingAttackTabs()
    {
        _wpf.Run(() =>
        {
            var panel = new ReinforcementsPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("SendTroopsTabControl"));
            var attacks = Assert.IsType<TabItem>(panel.FindName("IncomingAttacksTabItem"));
            var grid = Assert.IsType<DataGrid>(panel.FindName("IncomingAttackDataGrid"));
            var monitoredVillages = Assert.IsType<ItemsControl>(panel.FindName("IncomingAttackMonitoringVillageItemsControl"));
            var monitoredVillagesPanel = Assert.IsType<Border>(panel.FindName("IncomingAttackMonitoringPanel"));
            var infoIcon = Assert.IsType<ContentControl>(panel.FindName("IncomingAttacksInfoIcon"));
            var clearListButton = Assert.IsType<Button>(panel.FindName("ClearIncomingAttackListButton"));
            var toggleAllButton = Assert.IsType<Button>(panel.FindName("ToggleAllIncomingAttackMonitoringButton"));
            var soundAlert = Assert.IsType<CheckBox>(panel.FindName("IncomingAttackSoundAlertCheckBox"));
            var soundCooldown = Assert.IsType<ComboBox>(panel.FindName("IncomingAttackSoundCooldownComboBox"));
            var catapultInfoIcon = Assert.IsType<ContentControl>(panel.FindName("CatapultWavesInfoIcon"));

            Assert.Equal(3, tabs.Items.Count);
            Assert.Equal("Incoming attacks", attacks.Header);
            Assert.Equal(6, grid.Columns.Count);
            Assert.Single(grid.GroupStyle);
            Assert.False(grid.CanUserResizeColumns);
            Assert.All(grid.Columns, column => Assert.True(column.MinWidth >= 80));
            Assert.Equal("Player", grid.Columns[1].Header);
            Assert.Equal("Village", grid.Columns[2].Header);
            Assert.Equal("Clear list", clearListButton.Content);
            Assert.Equal("Toggle all", toggleAllButton.Content);
            Assert.False(soundAlert.IsChecked);
            Assert.Equal("1 min", Assert.IsType<ComboBoxItem>(soundCooldown.SelectedItem).Content);
            Assert.Equal(4, soundCooldown.Items.Count);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(Application.Current.FindResource("DangerBgBrush")).Color,
                Assert.IsType<SolidColorBrush>(clearListButton.Background).Color);
            var monitored = new IncomingAttackMonitoringVillageItem
            {
                VillageKey = "xy:1|2",
                VillageName = "BRO",
            };
            monitoredVillages.ItemsSource = new[] { monitored };
            tabs.SelectedItem = attacks;
            panel.Measure(new Size(1000, 700));
            panel.Arrange(new Rect(0, 0, 1000, 700));
            panel.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            var monitoringToggle = Assert.Single(
                FindVisualChildren<ToggleButton>(panel),
                toggle => ReferenceEquals(toggle.Tag, monitored));
            Assert.True(monitoringToggle.IsChecked);
            Assert.Equal(38, monitoringToggle.Width);
            Assert.Equal(22, monitoringToggle.Height);
            Assert.Equal(66, monitoredVillagesPanel.MinHeight);
            var typeColumn = Assert.IsType<DataGridTemplateColumn>(grid.Columns[0]);
            var typeCell = Assert.IsType<TextBlock>(typeColumn.CellTemplate.LoadContent());
            var typeTriggers = typeCell.Style.Triggers.OfType<DataTrigger>().ToList();
            var raidTrigger = Assert.Single(typeTriggers, trigger => Equals(trigger.Value, "Raid"));
            var attackTrigger = Assert.Single(typeTriggers, trigger => Equals(trigger.Value, "Attack"));
            Assert.Contains(raidTrigger.Setters.OfType<Setter>(), setter => setter.Property == TextBlock.ForegroundProperty);
            Assert.Contains(attackTrigger.Setters.OfType<Setter>(), setter => setter.Property == TextBlock.ForegroundProperty);
            Assert.Equal(
                "Incoming attacks and raids detected on Dorf1 and verified in Rally Point. Times use the Travian server clock.",
                infoIcon.ToolTip);
            Assert.Same(Application.Current.FindResource("InfoTooltipIconStyle"), infoIcon.Style);
            Assert.Same(Application.Current.FindResource("InfoTooltipIconStyle"), catapultInfoIcon.Style);
        });
    }

    [Fact]
    public void TroopsHubPanel_ContainsRequestedTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new TroopsHubPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("TroopsTabControl"));

            Assert.Equal(
                new[] { "Build Troops", "Upgrade troops", "Reinforcements", "Incoming attacks", "Evasion", "Catapult waves", "Brewery celebration" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void TroopEvasionPanel_OffersLockedTimingChoicesAndExpandableVillageList()
    {
        _wpf.Run(() =>
        {
            var panel = new TroopEvasionPanel();
            var lead = Assert.IsType<ComboBox>(panel.FindName("LeadTimeComboBox"));
            var protection = Assert.IsType<ComboBox>(panel.FindName("ProtectionWindowComboBox"));
            var targetX = Assert.IsType<TextBox>(panel.FindName("TargetXTextBox"));
            var targetY = Assert.IsType<TextBox>(panel.FindName("TargetYTextBox"));
            var movement = Assert.IsType<ComboBox>(panel.FindName("MovementTypeComboBox"));
            var evadeRaids = Assert.IsType<CheckBox>(panel.FindName("EvadeRaidsCheckBox"));
            var evadeAttacks = Assert.IsType<CheckBox>(panel.FindName("EvadeAttacksCheckBox"));
            var villages = Assert.IsType<ItemsControl>(panel.FindName("VillageItemsControl"));
            var descriptionInfo = Assert.IsType<ContentControl>(panel.FindName("TroopEvasionInfoIcon"));
            var leadInfo = Assert.IsType<ContentControl>(panel.FindName("LeadTimeInfoIcon"));
            var protectionInfo = Assert.IsType<ContentControl>(panel.FindName("ProtectionWindowInfoIcon"));

            Assert.Equal(new[] { 1, 2, 5, 10 }, lead.Items.Cast<int>().ToArray());
            Assert.Equal(new[] { 1, 2, 5, 10 }, protection.Items.Cast<int>().ToArray());
            Assert.Equal(Enum.GetValues<TroopEvasionMovementType>(), movement.Items.Cast<TroopEvasionMovementType>());
            Assert.True(evadeRaids.IsChecked);
            Assert.True(evadeAttacks.IsChecked);
            Assert.NotNull(descriptionInfo.ToolTip);
            Assert.NotNull(leadInfo.ToolTip);
            Assert.NotNull(protectionInfo.ToolTip);
            var standardInfoStyle = Application.Current.FindResource("InfoTooltipIconStyle");
            Assert.Same(standardInfoStyle, descriptionInfo.Style);
            Assert.Same(standardInfoStyle, leadInfo.Style);
            Assert.Same(standardInfoStyle, protectionInfo.Style);
            Assert.Same(panel.Villages, villages.ItemsSource);

            var village = TroopEvasionVillageItem.Create(
                new VillageSelectionItem { Name = "BRO", Url = "dorf1.php?newdid=1", Tribe = "Huns" },
                new TroopEvasionVillageSettings("xy:1|2", "BRO", "dorf1.php?newdid=1"));
            panel.Villages.Add(village);
            panel.SetGlobalSettings(5, 5, 12, -13, TroopEvasionMovementType.Raid, false, true);
            panel.Measure(new Size(1000, 700));
            panel.Arrange(new Rect(0, 0, 1000, 700));
            panel.UpdateLayout();

            Assert.Equal("12", targetX.Text);
            Assert.Equal("-13", targetY.Text);
            Assert.Equal("12", village.TargetX);
            Assert.Equal("-13", village.TargetY);
            Assert.Equal(TroopEvasionMovementType.Raid, village.MovementType);
            Assert.False(panel.EvadeRaids);
            Assert.True(panel.EvadeAttacks);
            Assert.Null(panel.FindName("PausedTextBlock"));
            var toggle = Assert.Single(FindVisualChildren<ToggleButton>(panel), item => ReferenceEquals(item.Tag, village));
            Assert.Equal(36, toggle.Width);
            Assert.Contains(FindVisualChildren<Button>(panel), button => Equals(button.Content, "Sync settings"));
            Assert.Contains(FindVisualChildren<Button>(panel), button => Equals(button.Content, "Validate"));
        });
    }

    [Fact]
    public void ResourcesHubPanel_ContainsResourcesAndTradingTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new ResourcesHubPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("ResourcesTabControl"));

            Assert.Equal(
                new[] { "Resources", "Trading" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void NpcTradeHubPanel_ContainsNpcAndSilverTradingTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new NpcTradeHubPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("NpcTradeTabControl"));

            Assert.Equal(
                new[] { "NPC", "Silver trading" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void HeroHubPanel_ContainsRequestedTabsInOrder()
    {
        _wpf.Run(() =>
        {
            var panel = new HeroHubPanel();
            var tabs = Assert.IsType<TabControl>(panel.FindName("HeroTabControl"));

            Assert.Equal(
                new[] { "Adventures", "Attributes", "Hero inventory" },
                tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
        });
    }

    [Fact]
    public void HeroInventoryTab_EmbedsVillageSettingsAndUsesHeaderRefreshIcon()
    {
        _wpf.Run(() =>
        {
            var hub = new HeroHubPanel { DataContext = new HeroViewModel() };
            var inventoryPanel = Assert.IsType<HeroPanel>(hub.FindName("HeroInventoryPanel"));
            var tabs = Assert.IsType<TabControl>(hub.FindName("HeroTabControl"));
            tabs.SelectedItem = hub.FindName("HeroInventoryTabItem");
            hub.Measure(new Size(1000, 700));
            hub.Arrange(new Rect(0, 0, 1000, 700));
            hub.UpdateLayout();

            var refresh = Assert.IsType<Button>(inventoryPanel.FindName("RefreshHeroInventoryButton"));
            Assert.Null(refresh.Content);
            Assert.Same(Application.Current.FindResource("RefreshIconButtonStyle"), refresh.Style);
            var maxPerResource = Assert.IsType<TextBox>(inventoryPanel.FindName("HeroResourceMaxLimitTextBox"));
            var toggleAllVillages = Assert.IsType<Button>(inventoryPanel.FindName("ToggleAllHeroResourcesButton"));
            var headerActions = Assert.IsType<StackPanel>(inventoryPanel.FindName("HeroResourceHeaderActionsPanel"));
            Assert.Same(headerActions, VisualTreeHelper.GetParent(maxPerResource));
            Assert.Same(headerActions, VisualTreeHelper.GetParent(toggleAllVillages));
            Assert.True(headerActions.Children.IndexOf(maxPerResource) < headerActions.Children.IndexOf(toggleAllVillages));
            Assert.DoesNotContain(
                FindVisualChildren<Button>(inventoryPanel),
                button => Equals(button.Content, "Save changes"));
            Assert.DoesNotContain(
                FindVisualChildren<Button>(inventoryPanel),
                button => Equals(button.Content, "Refresh hero inventory"));
        });
    }

    [Fact]
    public void HeroHubPanel_AdventurePickOrderCanBeChanged()
    {
        _wpf.Run(() =>
        {
            var vm = new HeroViewModel();
            var hub = new HeroHubPanel { DataContext = vm };
            var tabs = Assert.IsType<TabControl>(hub.FindName("HeroTabControl"));
            var panels = tabs.Items.Cast<TabItem>()
                .Select(item => Assert.IsType<HeroPanel>(item.Content))
                .ToList();
            foreach (var panel in panels)
            {
                panel.Measure(new Size(1000, 700));
                panel.Arrange(new Rect(0, 0, 1000, 700));
                panel.UpdateLayout();
            }
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            var adventurePanel = panels.Single(panel => panel.Section == "Adventures");
            var topFirst = FindVisualChildren<RadioButton>(adventurePanel)
                .Single(button => Equals(button.Content, "Top adventure first"));

            topFirst.IsChecked = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

            Assert.True(topFirst.IsChecked);
            Assert.True(vm.IsAdventurePickTop);
            Assert.False(vm.IsAdventurePickShortest);
        });
    }

    [Fact]
    public void HeroPanel_AutoAssignSettingIsInsideAttributeCard()
    {
        _wpf.Run(() =>
        {
            var panel = new HeroPanel();
            var adventureSettings = Assert.IsType<Border>(panel.FindName("SettingsCard"));
            var attributeAutomation = Assert.IsType<Border>(panel.FindName("AttributeAutomationCard"));

            Assert.DoesNotContain(
                FindVisualChildren<CheckBox>(adventureSettings),
                checkBox => Equals(checkBox.Content, "Auto assign attribute points"));
            Assert.Contains(
                FindVisualChildren<CheckBox>(attributeAutomation),
                checkBox => Equals(checkBox.Content, "Auto assign attribute points"));
        });
    }

    [Fact]
    public void HeroPanel_ShowsMaximumEditorAsTheRightmostAttributeColumn()
    {
        _wpf.Run(() =>
        {
            var vm = new HeroViewModel();
            vm.LoadSettingsFromConfig(new BotOptions
            {
                HeroStatMaximums = "resources=40,fighting_strength=100,offence_bonus=100,defence_bonus=100",
            });
            var panel = new HeroPanel { DataContext = vm };

            panel.Measure(new Size(1000, 700));
            panel.Arrange(new Rect(0, 0, 1000, 700));
            panel.UpdateLayout();

            Assert.Contains(FindVisualChildren<TextBlock>(panel), text => text.Text == "Max");
            var editors = FindVisualChildren<TextBox>(panel)
                .Where(textBox => textBox.DataContext is HeroAttributePriorityItem)
                .ToList();
            Assert.Equal(4, editors.Count);
            Assert.All(editors, editor => Assert.Equal(3, Grid.GetColumn(editor)));
            Assert.Equal(
                "40",
                Assert.Single(editors, editor =>
                    editor.DataContext is HeroAttributePriorityItem { Key: "resources" }).Text);
        });
    }

    [Fact]
    public void FarmingPanel_NextSendDisplay_BindsToHeadersCreatedAfterTheTimerUpdate()
    {
        _wpf.Run(() =>
        {
            var panel = new FarmingPanel();

            panel.SetNextSendDisplay("Next send: 01:00");
            var lists = new ObservableCollection<FarmListStatusRow>
            {
                new() { Name = "Test", TotalFarmCount = 1, VillageOrdinal = 0, VillageHeaderText = "Village" },
            };
            panel.FarmLists.ItemsSource = lists;
            var view = CollectionViewSource.GetDefaultView(lists);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FarmListStatusRow.VillageOrdinal)));
            panel.Measure(new Size(1280, 900));
            panel.Arrange(new Rect(0, 0, 1280, 900));
            panel.UpdateLayout();

            var nextSend = FindVisualChildren<TextBlock>(panel)
                .Single(element => Equals(element.Tag, "FarmListNextSendText"));
            Assert.Equal("Next send: 01:00", nextSend.Text);
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
