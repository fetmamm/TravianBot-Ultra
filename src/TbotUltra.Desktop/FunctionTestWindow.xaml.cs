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
    public event RoutedEventHandler? IncomingAttackSoundTestRequested;
    public event RoutedEventHandler? MoveLossFarmsTestRequested;
    public event RoutedEventHandler? CheckCapitalRequested;
    public event RoutedEventHandler? IncreaseAdventuresToHardRequested;
    public event RoutedEventHandler? ReduceAdventuresTimeRequested;
    public event RoutedEventHandler? StartAdventureRequested;
    public event RoutedEventHandler? BulkMessagesRequested;
    public event RoutedEventHandler? SavePageHtmlRequested;
    public event RoutedEventHandler? RunNewAccountAnalysisRequested;
    public event RoutedEventHandler? ClearNewAccountAnalysisRequested;
    public event RoutedEventHandler? UpdateVersionPreviewRequested;

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

    private void TestIncomingAttackSoundButton_Click(object sender, RoutedEventArgs e)
    {
        IncomingAttackSoundTestRequested?.Invoke(sender, e);
    }

    private void TestMoveLossFarmsButton_Click(object sender, RoutedEventArgs e)
    {
        MoveLossFarmsTestRequested?.Invoke(sender, e);
    }

    private void CheckCapitalButton_Click(object sender, RoutedEventArgs e)
    {
        CheckCapitalRequested?.Invoke(sender, e);
    }

    private void TestIncreaseAdventuresToHardButton_Click(object sender, RoutedEventArgs e)
    {
        IncreaseAdventuresToHardRequested?.Invoke(sender, e);
    }

    private void TestReduceAdventuresTimeButton_Click(object sender, RoutedEventArgs e)
    {
        ReduceAdventuresTimeRequested?.Invoke(sender, e);
    }

    private void StartAdventureButton_Click(object sender, RoutedEventArgs e)
    {
        StartAdventureRequested?.Invoke(sender, e);
    }

    private void BulkMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        BulkMessagesRequested?.Invoke(sender, e);
    }

    private void SavePageHtmlButton_Click(object sender, RoutedEventArgs e)
    {
        SavePageHtmlRequested?.Invoke(sender, e);
    }

    private void RunNewAccountAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        RunNewAccountAnalysisRequested?.Invoke(sender, e);
    }

    private void ClearNewAccountAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        ClearNewAccountAnalysisRequested?.Invoke(sender, e);
    }

    private void UpdateVersionPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateVersionPreviewRequested?.Invoke(sender, e);
    }

}
