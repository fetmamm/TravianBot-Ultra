using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Canonical manual-operation lifecycle shared by the Click-handler bodies:
    /// BeginOperation -> stopwatch -> StartOperation -> busy(true) ->
    /// body -> CompleteOperation, with OperationCanceledException mapped to a paused
    /// status (no failure mark) and other exceptions to FailOperation (swallowed).
    /// The finally order is load-bearing: busy(false) must run before
    /// DisposeOperationCts(), which may start the deferred session-pacing sleep.
    /// Only sites matching this shape exactly should call it; divergent flows keep
    /// their hand-written blocks.
    /// </summary>
    private async Task RunGuardedOperationAsync(
        string operationName,
        string pausedStatusText,
        Action<bool> toggleBusy,
        Func<string, CancellationToken, Task<string>> body)
    {
        var operationId = BeginOperation(operationName);
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        toggleBusy(true);
        try
        {
            var summary = await body(operationId, operationToken);
            CompleteOperation(operationId, operationSw, summary);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = pausedStatusText;
            AppendLog(pausedStatusText);
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            toggleBusy(false);
            DisposeOperationCts();
        }
    }
}
