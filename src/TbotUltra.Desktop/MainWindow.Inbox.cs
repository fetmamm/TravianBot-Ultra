using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    internal void OnInboxMarkMessagesReadClicked()
    {
        MarkMessagesReadCore();
    }

    internal void OnInboxMarkReportsReadClicked()
    {
        MarkReportsReadCore();
    }

    internal void OnInboxAutoReadChanged()
    {
        if (IsSessionSleeping)
        {
            return;
        }

        _backgroundTasks.Track(RefreshInboxIndicatorsAsync(logErrors: true, force: true));
    }

    private async void MarkMessagesReadCore()
        => await GuardUiAsync(MarkMessagesReadCoreAsync);

    private async Task MarkMessagesReadCoreAsync()
    {
        if (BlockIfSessionSleeping("Mark messages as read"))
        {
            return;
        }

        var operationId = BeginOperation("MarkMessagesRead");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
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
            DisposeOperationCts();
        }
    }

    private async void MarkReportsReadCore()
        => await GuardUiAsync(MarkReportsReadCoreAsync);

    private async Task MarkReportsReadCoreAsync()
    {
        if (BlockIfSessionSleeping("Mark reports as read"))
        {
            return;
        }

        var operationId = BeginOperation("MarkReportsRead");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
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
            DisposeOperationCts();
        }
    }

    private async Task RefreshInboxIndicatorsAsync(bool logErrors, bool force = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_loopController.IsClosing || IsSessionSleeping)
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
            var status = await _botService.ReadInboxStatusAsync(options, _ => { }, cancellationToken);
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
            await AutoReadInboxItemsAsync(options, status, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
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

    // Lightweight read-only check used by the ~20s background tick: only reads the
    // unread counters from the current page and updates the UI. Heavy auto-read
    // (which may navigate) stays on the dedicated 5-minute timer.
    private async Task RefreshInboxIndicatorsQuickAsync()
    {
        if (_loopController.IsClosing || !_inboxAutoEnabled || IsSessionSleeping)
        {
            return;
        }

        if (!await _inboxRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            var status = await _botService.ReadInboxStatusAsync(options, _ => { }, _loopController.AcquireSessionScopeToken());
            UpdateInboxButtons(status.UnreadMessages, status.UnreadReports);
        }
        catch (OperationCanceledException)
        {
            // Stop/Pause cancels session-scoped background reads.
        }
        catch (Exception ex)
        {
            AppendLog($"Quick inbox check skipped: {ex.Message}");
        }
        finally
        {
            _inboxRefreshGate.Release();
        }
    }

    private async Task AutoReadInboxItemsAsync(BotOptions options, InboxStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsSessionSleeping)
        {
            return;
        }

        var (autoReadMessages, autoReadReports) = GetAutoReadInboxSelectionSnapshot();
        if ((!autoReadMessages || status.UnreadMessages <= 0) && (!autoReadReports || status.UnreadReports <= 0))
        {
            return;
        }

        var selection = GetSelectedVillageSelectionSnapshot();
        var changed = false;

        if (autoReadMessages && status.UnreadMessages > 0)
        {
            try
            {
                await _botService.MarkMessagesAsReadAsync(options, AppendLog, selection.Name, selection.Url, cancellationToken);
                changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Auto-read messages failed: {ex.Message}");
            }
        }

        if (autoReadReports && status.UnreadReports > 0)
        {
            try
            {
                await _botService.MarkReportsAsReadAsync(options, AppendLog, selection.Name, selection.Url, cancellationToken);
                changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Auto-read reports failed: {ex.Message}");
            }
        }

        if (!changed)
        {
            return;
        }

        var refreshed = await _botService.ReadInboxStatusAsync(options, _ => { }, cancellationToken);
        UpdateInboxButtons(refreshed.UnreadMessages, refreshed.UnreadReports);
    }

    private (bool AutoReadMessages, bool AutoReadReports) GetAutoReadInboxSelectionSnapshot()
    {
        if (Dispatcher.CheckAccess())
        {
            return (_inboxViewModel.AutoReadMessages, _inboxViewModel.AutoReadReports);
        }

        return Dispatcher.Invoke(() => (_inboxViewModel.AutoReadMessages, _inboxViewModel.AutoReadReports));
    }

    private void UpdateInboxButtons(int unreadMessages, int unreadReports)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateInboxButtons(unreadMessages, unreadReports));
            return;
        }

        // The Messages / Reports cards bind to InboxVm.MessageUnreadText /
        // ReportsUnreadText, and the sidebar nav button picks up its
        // background, foreground, and tooltip via a Style.DataTrigger on
        // InboxVm.HasUnreadMessages plus a NavTooltip binding. Pushing the
        // counts into the view model is enough — the bindings handle the
        // rest.
        _inboxViewModel.UnreadMessages = unreadMessages;
        _inboxViewModel.UnreadReports = unreadReports;
    }
}
