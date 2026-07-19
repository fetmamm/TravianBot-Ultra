using System.Windows;
using System.Windows.Controls;
using TbotUltra.Desktop.Views;
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
        { nameof(BuildingsPanel), () => new BuildingsPanel() },
        { nameof(ResourcesPanel), () => new ResourcesPanel() },
        { nameof(HeroPanel), () => new HeroPanel() },
        { nameof(TroopsPanel), () => new TroopsPanel() },
        { nameof(FarmingPanel), () => new FarmingPanel() },
        { nameof(QueuePanel), () => new QueuePanel() },
        { nameof(LogsPanel), () => new LogsPanel() },
        { nameof(InboxPanel), () => new InboxPanel() },
        { nameof(NpcTradePanel), () => new NpcTradePanel() },
        { nameof(ReinforcementsPanel), () => new ReinforcementsPanel() },
        { nameof(BusyOverlayControl), () => new BusyOverlayControl() },
        { nameof(StoragePreflightPlanView), () => new StoragePreflightPlanView("Preflight", []) },
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
}
