using System.Windows;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private AccountAutomationHold? ActiveAccountHold()
    {
        var accountName = _accountStore.ActiveAccountName();
        return string.IsNullOrWhiteSpace(accountName)
            ? null
            : _accountAutomationHoldStore.Load(accountName);
    }

    private bool BlockIfActiveAccountOnHold(string operation)
    {
        var hold = ActiveAccountHold();
        if (hold is null)
        {
            return false;
        }

        RefreshAccountHoldUi();
        StatusTextBlock.Text = "Automation is stopped for this account. Manual review is required.";
        AppendLog($"[account-hold] {operation} blocked for account '{hold.AccountName}' ({hold.AccessState}).");
        return true;
    }

    private async Task HoldAccountAutomationAsync(AccountAccessException exception)
    {
        var hold = new AccountAutomationHold(
            exception.AccountName,
            exception.State.ToString(),
            exception.Message,
            DateTimeOffset.UtcNow);
        _accountAutomationHoldStore.Save(hold);

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
        _loopController.CancelVillageSwitch();
        _loopController.CancelSessionScope();

        await Dispatcher.InvokeAsync(() =>
        {
            _isLoggedIn = false;
            StartLoopButton.Content = "Start bot";
            SetLoopIndicator(false);
            UpdateLoginButtonsVisual(false);
            RefreshAccountHoldUi();
        });

        AppendLog(
            $"ALARM: Automation stopped for account '{exception.AccountName}'. " +
            $"Access state={exception.State}. Manual review and re-enable are required. Reason: {exception.Message}");
    }

    private void RefreshAccountHoldUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)RefreshAccountHoldUi);
            return;
        }

        var hold = ActiveAccountHold();
        AccountHoldBorder.Visibility = hold is null ? Visibility.Collapsed : Visibility.Visible;
        if (hold is null)
        {
            AccountHoldTextBlock.Text = string.Empty;
            LoginButton.IsEnabled = !_uiBusy;
            return;
        }

        AccountHoldTextBlock.Text =
            $"{hold.AccessState} at {hold.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. {hold.Reason}";
        LoginButton.IsEnabled = false;
    }

    private void ReenableAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var accountName = _accountStore.ActiveAccountName();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return;
        }

        _accountAutomationHoldStore.Clear(accountName);
        _loopController.ClearLoopStopRequest();
        _loopController.ClearQueueStopRequest();
        RefreshAccountHoldUi();
        UpdateLoginButtonsVisual(false);
        StatusTextBlock.Text = "Account re-enabled. Press Login after completing manual review.";
        AppendLog($"Account '{accountName}' manually re-enabled. Queue and settings were kept.");
    }
}
