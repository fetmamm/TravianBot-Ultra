using System.Text.RegularExpressions;

namespace TbotUltra.Desktop;

// Tracking of manual (button-triggered) function executions so they appear in the
// queue UI as a synthetic runtime task with a success/failure/cancel outcome.
// Extracted verbatim from MainWindow.xaml.cs to keep that file focused; same
// class, so this is a pure relocation with no behavior change.
public partial class MainWindow
{
    private void EnsureManualExecutionTracking()
    {
        if (_activeManualExecution is not null)
        {
            return;
        }

        var operationId = _pendingManualOperationId;
        var operationName = operationId is not null && _operationNamesById.TryGetValue(operationId, out var knownName)
            ? knownName
            : "Manual function";

        operationId ??= $"manual-{Guid.NewGuid():N}";
        var taskName = BuildRuntimeManualTaskName(operationName);
        var queueItem = _botService.EnqueueRuntime(taskName, operationName, payload: null, priority: 0, maxRetries: 0);
        _botService.MarkQueueItemRunning(queueItem.Id);

        _activeManualExecution = new ManualExecutionState
        {
            OperationId = operationId,
            OperationName = operationName,
            QueueItemId = queueItem.Id,
            Outcome = ManualExecutionOutcome.None,
        };

        _pendingManualOperationId = null;
        SetActiveFunctionExecution(operationName);
        RefreshQueueUiOnUiThread(queueItem.Id);
    }

    private void CompleteManualExecutionTrackingIfNeeded()
    {
        if (_activeManualExecution is null)
        {
            return;
        }

        var execution = _activeManualExecution;
        try
        {
            switch (execution.Outcome)
            {
                case ManualExecutionOutcome.Succeeded:
                    _botService.MarkQueueItemSucceeded(execution.QueueItemId);
                    break;
                case ManualExecutionOutcome.Failed:
                    _botService.MarkQueueItemExecutionFailed(execution.QueueItemId);
                    break;
                default:
                    _botService.MarkQueueItemCanceled(execution.QueueItemId);
                    break;
            }
        }
        finally
        {
            _activeManualExecution = null;
            _operationNamesById.Remove(execution.OperationId);
            SetActiveFunctionExecution(null);
            RefreshQueueUiOnUiThread(execution.QueueItemId);
        }
    }

    private void SetManualExecutionOutcome(string operationId, ManualExecutionOutcome outcome)
    {
        if (_activeManualExecution is null)
        {
            return;
        }

        if (!string.Equals(_activeManualExecution.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeManualExecution.Outcome = outcome;
    }

    private static string BuildRuntimeManualTaskName(string operationName)
    {
        var normalized = Regex.Replace(operationName ?? string.Empty, "[^a-zA-Z0-9]+", "_")
            .Trim('_')
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "manual";
        }

        return $"{RuntimeManualTaskPrefix}:{normalized}";
    }
}
