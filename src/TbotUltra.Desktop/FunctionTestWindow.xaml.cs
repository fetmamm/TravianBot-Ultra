using System.Windows;

namespace TbotUltra.Desktop;

public partial class FunctionTestWindow : Window
{
    public event RoutedEventHandler? ResourceProductionTestRequested;
    public event RoutedEventHandler? NavigateToBreweryTestRequested;
    public event RoutedEventHandler? StartCelebrationTestRequested;
    public event RoutedEventHandler? NpcTradeBarracksTestRequested;

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
}
