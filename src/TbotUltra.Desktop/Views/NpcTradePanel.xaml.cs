using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop.Views;

public partial class NpcTradePanel : UserControl
{
    private MainWindow? _host;

    public NpcTradePanel() => InitializeComponent();

    private MainWindow? Host => _host ??= Window.GetWindow(this) as MainWindow;

    internal TextBlock GoldSpent => NpcTradeGoldSpentTextBlock;
    internal TextBlock TroopsCount => NpcTradeTroopsTextBlock;
    internal TextBlock BuildingsCount => NpcTradeBuildingsTextBlock;
    internal ComboBox TransferTargetVillage => ResourceTransferTargetVillageComboBox;
    internal ItemsControl TransferSourceVillages => ResourceTransferSourceVillagesItemsControl;
    internal ComboBox SourceThreshold => ResourceTransferSourceThresholdComboBox;
    internal ComboBox SourceKeep => ResourceTransferSourceKeepComboBox;
    internal ComboBox TargetFill => ResourceTransferTargetFillComboBox;
    internal CheckBox TransferWood => ResourceTransferWoodCheckBox;
    internal CheckBox TransferClay => ResourceTransferClayCheckBox;
    internal CheckBox TransferIron => ResourceTransferIronCheckBox;
    internal CheckBox TransferCrop => ResourceTransferCropCheckBox;
    internal TextBlock TransferStatus => ResourceTransferStatusTextBlock;
    internal Button TransferQueueNow => ResourceTransferQueueNowButton;
    internal Button TransferScanVillages => ResourceTransferScanVillagesButton;

    private void ResourceTransferSetting_Changed(object sender, RoutedEventArgs e) => Host?.OnResourceTransferSettingChanged(sender, e);
    private void ResourceTransferSetting_SelectionChanged(object sender, SelectionChangedEventArgs e) => Host?.OnResourceTransferSettingSelectionChanged(sender, e);
    private void QueueResourceTransferNowButton_Click(object sender, RoutedEventArgs e) => Host?.OnQueueResourceTransferNowClicked(sender, e);
    private void ResourceTransferScanVillagesButton_Click(object sender, RoutedEventArgs e) => Host?.OnResourceTransferScanVillagesClicked(sender, e);
    private void TownHallSettingsButton_Click(object sender, RoutedEventArgs e) => Host?.OnTownHallSettingsClicked(sender, e);
}
