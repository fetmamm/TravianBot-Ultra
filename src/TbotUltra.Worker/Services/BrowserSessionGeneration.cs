namespace TbotUltra.Worker.Services;

internal sealed class BrowserSessionGeneration
{
    private long _value;

    internal long Capture() => Volatile.Read(ref _value);

    internal long Invalidate() => Interlocked.Increment(ref _value);

    internal void ThrowIfStale(long captured)
    {
        if (captured != Capture())
        {
            throw new OperationCanceledException(
                "Browser session creation was superseded by a shutdown or account transition.");
        }
    }
}
