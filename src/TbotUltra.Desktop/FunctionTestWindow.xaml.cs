using System.Windows;

namespace TbotUltra.Desktop;

public partial class FunctionTestWindow : Window
{
    public event RoutedEventHandler? ResourceProductionTestRequested;
    public event RoutedEventHandler? NavigateToBreweryTestRequested;
    public event RoutedEventHandler? StartCelebrationTestRequested;
    public event RoutedEventHandler? NpcTradeBarracksTestRequested;
    public event RoutedEventHandler? NpcTradeBuildingTestRequested;
    public event RoutedEventHandler? ReadSmithyQueueTestRequested;
    public event RoutedEventHandler? ReinforcementsTestRequested;

    public FunctionTestWindow()
    {
        InitializeComponent();
    }

    private void TestResourceProductionButton_Click(object sender, RoutedEventArgs e)
    {
        ResourceProductionTestRequested?.Invoke(sender, e);
    }

    private void TestNavigateToBreweryButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToBreweryTestRequested?.Invoke(sender, e);
    }

    private void TestStartCelebrationButton_Click(object sender, RoutedEventArgs e)
    {
        StartCelebrationTestRequested?.Invoke(sender, e);
    }

    private void TestNpcTradeBarracksButton_Click(object sender, RoutedEventArgs e)
    {
        NpcTradeBarracksTestRequested?.Invoke(sender, e);
    }

    private void TestNpcTradeBuildingButton_Click(object sender, RoutedEventArgs e)
    {
        NpcTradeBuildingTestRequested?.Invoke(sender, e);
    }

    private void TestReadSmithyQueueButton_Click(object sender, RoutedEventArgs e)
    {
        ReadSmithyQueueTestRequested?.Invoke(sender, e);
    }

    private void TestReinforcementsButton_Click(object sender, RoutedEventArgs e)
    {
        ReinforcementsTestRequested?.Invoke(sender, e);
    }
}
