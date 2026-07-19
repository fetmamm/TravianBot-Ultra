using System;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    // Shared dispatcher-marshalling helpers. Each mirrors one existing hand-rolled idiom exactly;
    // do not add a normalizing variant that changes when work runs relative to the caller.

    // Run now on the UI thread: inline when already there, otherwise blocking Dispatcher.Invoke.
    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.Invoke(action);
    }

    // Same as RunOnUi(Action) but returns the result to the calling thread.
    private T RunOnUi<T>(Func<T> func)
    {
        return Dispatcher.CheckAccess() ? func() : Dispatcher.Invoke(func);
    }

    // Run inline when already on the UI thread; otherwise queue fire-and-forget via BeginInvoke.
    // Off-thread callers do NOT wait for completion — matches the existing post-and-return idiom.
    private void RunOrPostToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(action);
    }
}
