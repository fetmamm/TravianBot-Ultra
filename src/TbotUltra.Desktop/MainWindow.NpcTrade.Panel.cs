using System.Windows;
using System.Windows.Controls;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private TextBlock NpcTradeGoldSpentTextBlock => NpcTradePanelControl.GoldSpent;
    private TextBlock NpcTradeTroopsTextBlock => NpcTradePanelControl.TroopsCount;
    private TextBlock NpcTradeBuildingsTextBlock => NpcTradePanelControl.BuildingsCount;
    private ComboBox ResourceTransferTargetVillageComboBox => NpcTradePanelControl.TransferTargetVillage;
    private ItemsControl ResourceTransferSourceVillagesItemsControl => NpcTradePanelControl.TransferSourceVillages;
    private ComboBox ResourceTransferSourceThresholdComboBox => NpcTradePanelControl.SourceThreshold;
    private ComboBox ResourceTransferSourceKeepComboBox => NpcTradePanelControl.SourceKeep;
    private ComboBox ResourceTransferTargetFillComboBox => NpcTradePanelControl.TargetFill;
    private CheckBox ResourceTransferWoodCheckBox => NpcTradePanelControl.TransferWood;
    private CheckBox ResourceTransferClayCheckBox => NpcTradePanelControl.TransferClay;
    private CheckBox ResourceTransferIronCheckBox => NpcTradePanelControl.TransferIron;
    private CheckBox ResourceTransferCropCheckBox => NpcTradePanelControl.TransferCrop;
    private TextBlock ResourceTransferStatusTextBlock => NpcTradePanelControl.TransferStatus;
    private Button ResourceTransferQueueNowButton => NpcTradePanelControl.TransferQueueNow;
    private Button ResourceTransferScanVillagesButton => NpcTradePanelControl.TransferScanVillages;

    internal void OnResourceTransferSettingChanged(object sender, RoutedEventArgs e) => ResourceTransferSetting_Changed(sender, e);
    internal void OnResourceTransferSettingSelectionChanged(object sender, SelectionChangedEventArgs e) => ResourceTransferSetting_SelectionChanged(sender, e);
    internal void OnQueueResourceTransferNowClicked(object sender, RoutedEventArgs e) => QueueResourceTransferNowButton_Click(sender, e);
    internal void OnResourceTransferScanVillagesClicked(object sender, RoutedEventArgs e) => ResourceTransferScanVillagesButton_Click(sender, e);
}
