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
    public event RoutedEventHandler? IncreaseAdventuresToHardRequested;
    public event RoutedEventHandler? ReduceAdventuresTimeRequested;
    public event RoutedEventHandler? BulkMessagesRequested;
    public event RoutedEventHandler? SavePageHtmlRequested;

    public FunctionTestWindow()
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
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

    private void TestIncreaseAdventuresToHardButton_Click(object sender, RoutedEventArgs e)
    {
        IncreaseAdventuresToHardRequested?.Invoke(sender, e);
    }

    private void TestReduceAdventuresTimeButton_Click(object sender, RoutedEventArgs e)
    {
        ReduceAdventuresTimeRequested?.Invoke(sender, e);
    }

    private void BulkMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        BulkMessagesRequested?.Invoke(sender, e);
    }

    private void SavePageHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        SavePageHtmlRequested?.Invoke(sender, e);
    }

}
