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
/// MainWindow still owns its <c>_loopCts</c>/<c>_operationCts</c>/etc. fields
/// for now; subsequent commits will move that lifecycle here as well.
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
