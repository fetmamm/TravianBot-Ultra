using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TbotUltra.Desktop.Services.Orchestration;

/// <summary>
/// Owns concurrency primitives that govern the desktop UI's continuous loop
/// and queue auto-run. This is the first slice of the broader LoopController
/// extraction (analysis problem 3.3); it currently centralizes:
///
///   - the queue auto-run gate (a SemaphoreSlim) with structured logging of
///     Wait/Release pairs so deadlocks become visible in the session log
///   - an "is-closing" flag that dispatcher callbacks can short-circuit on
///   - a CancellationTokenSource factory that emits a creation log line so we
///     can correlate cancellations with their origin in production
///
/// All cancellation-token-source lifecycles are now owned here: the manual
/// operation (<c>StartOperation</c>), the continuous loop (<c>StartLoop</c>),
/// village switches (<c>StartVillageSwitch</c>) and the queue auto-run root +
/// linked child (<c>StartAutoQueueRun</c>). MainWindow drives them through these
/// methods and no longer holds CTS fields of its own.
/// </summary>
public sealed class LoopController : IDisposable
{
    private readonly SemaphoreSlim _queueAutoRunGate = new(1, 1);
    private long _gateAcquireSeq;
    private bool _disposed;
    private volatile bool _loopStopRequested;
    private volatile bool _queueStopRequested;

    /// <summary>
    /// Optional sink for structured log lines. Defaults to <see cref="Debug.WriteLine(string)"/>
    /// so the controller can be used before MainWindow's logger is wired up.
    /// MainWindow assigns this to <c>AppendLog</c> after InitializeComponent.
    /// </summary>
    public Action<string> Logger { get; set; } = static line => Debug.WriteLine(line);

    /// <summary>
    /// True once <see cref="MarkClosing"/> has been invoked. Dispatcher
    /// callbacks should check this before touching UI state to avoid acting
    /// on a window that is being torn down.
    /// </summary>
    public bool IsClosing { get; private set; }

    /// <summary>
    /// Marks the controller as closing. Idempotent.
    /// </summary>
    public void MarkClosing()
    {
        if (IsClosing)
        {
            return;
        }

        IsClosing = true;
        Logger.Invoke("[loop] Controller marked closing.");
    }

    /// <summary>
    /// True when a graceful stop of the continuous-loop runner has been
    /// requested. The runner polls this between ticks and exits cleanly.
    /// </summary>
    public bool LoopStopRequested => _loopStopRequested;

    /// <summary>
    /// True when a graceful stop of the queue auto-runner has been requested.
    /// The runner polls this between items and exits cleanly.
    /// </summary>
    public bool QueueStopRequested => _queueStopRequested;

    /// <summary>
    /// Sets <see cref="LoopStopRequested"/>. Idempotent; logs only on
    /// transition from false to true.
    /// </summary>
    public void RequestLoopStop()
    {
        if (_loopStopRequested)
        {
            return;
        }

        _loopStopRequested = true;
        Logger.Invoke("[loop] Loop stop requested.");
    }

    /// <summary>
    /// Sets <see cref="QueueStopRequested"/>. Idempotent; logs only on
    /// transition from false to true.
    /// </summary>
    public void RequestQueueStop()
    {
        if (_queueStopRequested)
        {
            return;
        }

        _queueStopRequested = true;
        Logger.Invoke("[loop] Queue stop requested.");
    }

    /// <summary>
    /// Clears <see cref="LoopStopRequested"/> ahead of starting a new run.
    /// </summary>
    public void ClearLoopStopRequest() => _loopStopRequested = false;

    /// <summary>
    /// Clears <see cref="QueueStopRequested"/> ahead of starting a new run.
    /// </summary>
    public void ClearQueueStopRequest() => _queueStopRequested = false;

    /// <summary>
    /// Tries to acquire the queue-auto-run gate without blocking. Returns a
    /// disposable lease on success; returns <c>null</c> if the gate is held
    /// or the controller is disposed/closing. Hold time is logged on release
    /// so spikes become visible.
    /// </summary>
    public async Task<GateLease?> TryAcquireQueueAutoRunGateAsync(CancellationToken cancellationToken)
    {
        if (_disposed || IsClosing)
        {
            return null;
        }

        try
        {
            if (!await _queueAutoRunGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var seq = Interlocked.Increment(ref _gateAcquireSeq);
        Logger.Invoke($"[loop] Gate acquired #{seq}.");
        return new GateLease(this, seq, Stopwatch.StartNew());
    }

    private void ReleaseQueueAutoRunGate(long seq, Stopwatch holdTimer)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queueAutoRunGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Gate was disposed between acquire and release; ignore.
            return;
        }

        Logger.Invoke($"[loop] Gate released #{seq} (held {holdTimer.ElapsedMilliseconds} ms).");
    }

    /// <summary>
    /// Creates a new <see cref="CancellationTokenSource"/> with a label for
    /// log correlation. Use the label to identify which operation owns the
    /// token in trace output.
    /// </summary>
    public CancellationTokenSource CreateCts(string label)
    {
        var cts = new CancellationTokenSource();
        Logger.Invoke($"[loop] CTS created: {label}.");
        return cts;
    }

    private CancellationTokenSource? _operationCts;

    /// <summary>
    /// Starts a new manual-operation scope and returns its token. Replaces the
    /// current operation CTS field directly (callers historically guarded against
    /// concurrent operations elsewhere), mirroring the previous MainWindow
    /// behavior exactly. Always pair with <see cref="DisposeOperation"/> in a
    /// finally block.
    /// </summary>
    public CancellationToken StartOperation(string label)
    {
        _operationCts = CreateCts(label);
        return _operationCts.Token;
    }

    /// <summary>True while a manual-operation CTS is active.</summary>
    public bool HasActiveOperation => _operationCts is not null;

    /// <summary>Requests cancellation of the active operation, if any.</summary>
    public void CancelOperation() => _operationCts?.Cancel();

    /// <summary>Disposes the active operation CTS and clears it. Idempotent.</summary>
    public void DisposeOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    // --- Continuous-loop CTS ---

    private CancellationTokenSource? _loopCts;

    /// <summary>
    /// Cancels and disposes any prior continuous-loop CTS, then starts a new one
    /// and returns its token. A loop restart always happens after the previous
    /// loop task has completed, so the old token is no longer awaited and can be
    /// disposed safely — this prevents one undisposed CTS leaking per loop start.
    /// </summary>
    public CancellationToken StartLoop(string label)
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = CreateCts(label);
        return _loopCts.Token;
    }

    /// <summary>Requests cancellation of the continuous loop, if any.</summary>
    public void CancelLoop() => _loopCts?.Cancel();

    // --- Village-switch CTS ---

    private CancellationTokenSource? _villageSwitchCts;

    /// <summary>
    /// Cancels and disposes any prior village-switch CTS, then starts a new one
    /// and returns its token.
    /// </summary>
    public CancellationToken StartVillageSwitch(string label)
    {
        _villageSwitchCts?.Cancel();
        _villageSwitchCts?.Dispose();
        _villageSwitchCts = CreateCts(label);
        return _villageSwitchCts.Token;
    }

    /// <summary>Requests cancellation of the active village switch, if any.</summary>
    public void CancelVillageSwitch() => _villageSwitchCts?.Cancel();

    /// <summary>Disposes the village-switch CTS and clears it. Idempotent.</summary>
    public void DisposeVillageSwitch()
    {
        _villageSwitchCts?.Dispose();
        _villageSwitchCts = null;
    }

    // --- Queue auto-run CTS (long-lived root + per-run linked child) ---

    private readonly CancellationTokenSource _queueAutoRunCts = new();
    private CancellationTokenSource? _autoQueueRunCts;

    /// <summary>The long-lived root token, cancelled when the window closes.</summary>
    public CancellationToken QueueAutoRunRootToken => _queueAutoRunCts.Token;

    /// <summary>
    /// Starts a queue auto-run scope linked to the root token and returns its
    /// token. Pair with <see cref="DisposeAutoQueueRun"/> in a finally block.
    /// </summary>
    public CancellationToken StartAutoQueueRun()
    {
        _autoQueueRunCts = CancellationTokenSource.CreateLinkedTokenSource(_queueAutoRunCts.Token);
        return _autoQueueRunCts.Token;
    }

    /// <summary>Requests cancellation of the active queue auto-run, if any.</summary>
    public void CancelAutoQueueRun() => _autoQueueRunCts?.Cancel();

    /// <summary>Disposes the active queue-auto-run CTS and clears it. Idempotent.</summary>
    public void DisposeAutoQueueRun()
    {
        _autoQueueRunCts?.Dispose();
        _autoQueueRunCts = null;
    }

    /// <summary>Cancels the long-lived queue-auto-run root (window closing).</summary>
    public void CancelQueueAutoRunRoot() => _queueAutoRunCts.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var cts in new[] { _operationCts, _loopCts, _villageSwitchCts, _autoQueueRunCts, _queueAutoRunCts })
        {
            try
            {
                cts?.Dispose();
            }
            catch
            {
                // Disposing twice is harmless; swallow.
            }
        }

        try
        {
            _queueAutoRunGate.Dispose();
        }
        catch
        {
            // Disposing twice is harmless; swallow.
        }
    }

    /// <summary>
    /// Disposable handle for the queue-auto-run gate. Disposing releases the
    /// semaphore exactly once and logs hold time. Safe to dispose more than
    /// once.
    /// </summary>
    public sealed class GateLease : IDisposable
    {
        private readonly LoopController _controller;
        private readonly long _seq;
        private readonly Stopwatch _holdTimer;
        private bool _released;

        internal GateLease(LoopController controller, long seq, Stopwatch holdTimer)
        {
            _controller = controller;
            _seq = seq;
            _holdTimer = holdTimer;
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _controller.ReleaseQueueAutoRunGate(_seq, _holdTimer);
        }
    }
}
