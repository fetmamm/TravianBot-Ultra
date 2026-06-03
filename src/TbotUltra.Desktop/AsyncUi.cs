using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace TbotUltra.Desktop;

/// <summary>
/// Helpers for safely running asynchronous work from <c>async void</c> event
/// handlers.
///
/// App.xaml.cs already installs a global last-resort handler
/// (<see cref="System.Windows.Application.DispatcherUnhandledException"/>) that
/// keeps the process alive when an <c>async void</c> handler throws. Routing a
/// handler's body through <see cref="GuardAsync"/> adds a second, more useful
/// layer: the failure is surfaced in the in-app session log (with the handler
/// name) instead of only the on-disk crash log, which makes day-to-day
/// debugging far easier. <see cref="OperationCanceledException"/> is treated as
/// normal control flow and ignored.
/// </summary>
internal static class AsyncUi
{
    /// <summary>
    /// Awaits <paramref name="action"/> and logs any unhandled exception via
    /// <paramref name="log"/> instead of letting it escape the calling
    /// <c>async void</c> handler. <paramref name="caller"/> is captured
    /// automatically from the calling member name for context.
    /// </summary>
    public static async Task GuardAsync(
        Func<Task> action,
        Action<string> log,
        [CallerMemberName] string caller = "")
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal control-flow signal, not an error.
        }
        catch (Exception ex)
        {
            log($"[ui] Unhandled exception in {caller}: {ex.Message}");
        }
    }
}
