using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async void MarkMessagesReadButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("MarkMessagesRead");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            await _botService.MarkMessagesAsReadAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                operationToken);
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            CompleteOperation(operationId, operationSw, "Messages marked as read.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Mark as read paused.";
            AppendLog("Mark as read paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async void MarkReportsReadButton_Click(object sender, RoutedEventArgs e)
    {
        var operationId = BeginOperation("MarkReportsRead");
        var operationSw = Stopwatch.StartNew();
        _operationCts = new CancellationTokenSource();
        var operationToken = _operationCts.Token;
        ToggleUiBusy(true);
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            await EnsureChromiumInstalledAsync();
            await _botService.MarkReportsAsReadAsync(
                options,
                AppendLog,
                GetSelectedVillageName(),
                GetSelectedVillageUrl(),
                operationToken);
            await RefreshInboxIndicatorsAsync(logErrors: true, force: true);
            CompleteOperation(operationId, operationSw, "Reports marked as read.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Mark reports as read paused.";
            AppendLog("Mark reports as read paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            ToggleUiBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task RefreshInboxIndicatorsAsync(bool logErrors, bool force = false)
    {
        if (_isAppClosing)
        {
            return;
        }

        if (!await _inboxRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!_inboxAutoEnabled)
            {
                return;
            }

            if (!force && (_uiBusy || _autoQueueRunning || (_loopTask is not null && !_loopTask.IsCompleted)))
            {
                return;
            }

            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await _botService.ReadInboxStatusAsync(options, _ => { }, CancellationToken.None);
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
        }
        catch (Exception ex)
        {
            UpdateInboxButtons(0, 0);
            if (logErrors)
            {
                AppendLog($"Could not refresh unread messages/reports: {ex.Message}");
            }
        }
        finally
        {
            _inboxRefreshGate.Release();
        }
    }

    private async Task HandleInboxRefreshTickAsync()
    {
        await RefreshInboxIndicatorsAsync(logErrors: false);
    }

    private void UpdateInboxButtons(int unreadMessages, int unreadReports)
    {
        _lastUnreadMessages = unreadMessages;
        _lastUnreadReports = unreadReports;
        MessageUnreadTextBlock.Text = $"Unread: {unreadMessages}";
        ReportsUnreadTextBlock.Text = $"Unread: {unreadReports}";
        InboxNavButton.ToolTip = $"Messages {unreadMessages} | Reports {unreadReports}";

        if (unreadMessages > 0)
        {
            InboxNavButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            InboxNavButton.Foreground = Brushes.White;
        }
        else
        {
            InboxNavButton.Background = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            InboxNavButton.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
        }
    }
}
