using System.Diagnostics;
using System.IO;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private string ReportsPngDirectory => Path.Combine(_projectRoot, "Reports");

    internal void OnInboxSaveReportPngClicked()
    {
        if (BlockIfSessionSleeping("Save report as PNG"))
        {
            return;
        }

        if (IsReportPngBlockedByRunningWork())
        {
            ShowSaveReportPngPausedDialog(this);
            return;
        }

        if (!_isLoggedIn)
        {
            AppDialog.Show(this, "Log in first, then open the report you want to save.", "Save report as PNG", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OpenSaveReportPngWindow();
    }

    private void OpenSaveReportPngWindow()
    {
        if (_saveReportPngWindow is not null)
        {
            if (!_saveReportPngWindow.IsVisible)
            {
                _saveReportPngWindow.Show();
            }

            _saveReportPngWindow.Activate();
            return;
        }

        _saveReportPngWindow = new SaveReportPngWindow(ReportsPngDirectory)
        {
            Owner = this,
        };
        _saveReportPngWindow.SaveRequested += SaveReportPngWindow_SaveRequested;
        _saveReportPngWindow.Closed += (_, _) =>
        {
            if (_saveReportPngWindow is not null)
            {
                _saveReportPngWindow.SaveRequested -= SaveReportPngWindow_SaveRequested;
                _saveReportPngWindow = null;
            }
        };
        _saveReportPngWindow.Show();
        _saveReportPngWindow.Activate();
    }

    private async void SaveReportPngWindow_SaveRequested(object? sender, SaveReportPngRequest request)
        => await GuardUiAsync(() => SaveReportPngWindowSaveRequestedAsync(sender, request));

    private async Task SaveReportPngWindowSaveRequestedAsync(object? sender, SaveReportPngRequest request)
    {
        if (BlockIfSessionSleeping("Save report as PNG"))
        {
            return;
        }

        var dialog = sender as SaveReportPngWindow;
        var owner = (Window?)dialog ?? this;
        if (IsReportPngBlockedByRunningWork())
        {
            ShowSaveReportPngPausedDialog(owner);
            dialog?.SetSaveResult("Bot must be paused.");
            return;
        }

        if (!_isLoggedIn)
        {
            AppDialog.Show(owner, "Log in first, then open the report you want to save.", "Save report as PNG", MessageBoxButton.OK, MessageBoxImage.Warning);
            dialog?.SetSaveResult("Log in first.");
            return;
        }

        var operationId = BeginOperation("SaveReportPng");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        dialog?.SetSaveInProgress(true);
        try
        {
            Directory.CreateDirectory(ReportsPngDirectory);
            var fileName = $"report_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(ReportsPngDirectory, fileName);
            var options = LoadBotOptions();

            AppendLog($"[{operationId}] capturing current report as PNG.");
            var result = await _botService.SaveReportScreenshotAsync(
                options,
                filePath,
                request.HideAttacker,
                request.HideDefender,
                AppendLog,
                operationToken);

            if (!result.IsReportPage)
            {
                AppDialog.Show(
                    owner,
                    "You are not on a report page. Navigate to the report you want to save and try again.",
                    "Save report as PNG",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dialog?.SetSaveResult("Not on a report page.");
                AppendLog($"[{operationId}] skipped: not on an opened report page. url='{result.Url}'");
                CompleteOperation(operationId, operationSw, "Save report as PNG skipped: wrong page.");
                return;
            }

            dialog?.SetSaveResult($"Saved {fileName}");
            CompleteOperation(operationId, operationSw, $"Saved report PNG to {result.FilePath}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Save report as PNG canceled.");
            dialog?.SetSaveResult("Save canceled.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            dialog?.SetSaveResult($"Save failed: {ex.Message}");
        }
        finally
        {
            DisposeOperationCts();
            dialog?.SetSaveInProgress(false);
        }
    }

    private bool IsReportPngBlockedByRunningWork()
    {
        return _autoQueueRunning
            || (_loopTask is not null && !_loopTask.IsCompleted)
            || _loopController.HasActiveOperation;
    }

    private static void ShowSaveReportPngPausedDialog(Window owner)
    {
        AppDialog.Show(
            owner,
            "The bot must be paused to save a report as PNG. Stop the bot and try again.",
            "Save report as PNG",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
