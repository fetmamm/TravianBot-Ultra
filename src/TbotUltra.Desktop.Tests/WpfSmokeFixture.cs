using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Owns the single STA thread the smoke tests run on. WPF objects have thread affinity and
/// <see cref="Application.Current"/> may only be created once per process, so every smoke test has to
/// share one thread and one Application instance. The App-level theme dictionaries are merged exactly
/// as App.xaml does, so a control constructed here resolves StaticResource lookups the same way it
/// does at runtime — that is what makes these tests able to catch a missing or misspelled style key.
/// </summary>
public sealed class WpfSmokeFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    // Same list, same order as App.xaml. Kept in sync manually; StyleResourcesSmokeTests asserts the
    // keys the app actually uses resolve, which is what would break if this drifted.
    private static readonly string[] ThemeDictionaries =
    [
        "Themes/Palette.xaml",
        "Themes/BaseControls.xaml",
        "Themes/Inputs.xaml",
        "Themes/ScrollBars.xaml",
        "Themes/Buttons.xaml",
        "Themes/Toggles.xaml",
        "Themes/Badges.xaml",
        "Themes/Tooltips.xaml",
    ];

    public WpfSmokeFixture()
    {
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            if (Application.Current is null)
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var source in ThemeDictionaries)
                {
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/TbotUltra.Desktop;component/{source}"),
                    });
                }
            }

            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-smoke",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "WPF smoke thread failed to start.");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the shared STA thread and rethrows anything it threw, so a
    /// XAML parse error surfaces as a normal test failure instead of a silent background crash.
    /// </summary>
    public void Run(Action action)
    {
        Exception? failure = null;
        var completed = _dispatcher!.Invoke(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                return true;
            },
            DispatcherPriority.Normal,
            CancellationToken.None,
            TimeSpan.FromSeconds(60));

        Assert.True(completed, "Smoke action timed out on the WPF thread.");
        if (failure is not null)
        {
            throw new InvalidOperationException($"Smoke action failed: {failure.Message}", failure);
        }
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Binds every smoke test class to the one shared WPF thread. xUnit runs a collection serially, which
/// is required here — the tests share one Application and one dispatcher.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfSmokeCollection : ICollectionFixture<WpfSmokeFixture>
{
    public const string Name = "wpf-smoke";
}
