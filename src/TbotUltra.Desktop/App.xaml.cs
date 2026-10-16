using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static ServiceProvider? _serviceProvider;
    private bool _hasLoadedMainWindow;
    private bool _startupCanceled;
    private StartupProgressWindow? _startupProgressWindow;
    private Mutex? _copyInstanceMutex;

    /// <summary>
    /// Application-wide service provider. Populated in <see cref="OnStartup"/>
    /// before the StartupUri creates MainWindow. Use this from code that needs
    /// to resolve services that are not yet wired through constructor injection.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var copyPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var copyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(copyPath)))[..24];
        _copyInstanceMutex = new Mutex(true, $"Local\\TbotUltra-{copyHash}", out var ownsCopyInstance);
        if (!ownsCopyInstance)
        {
            _copyInstanceMutex.Dispose();
            _copyInstanceMutex = null;
            MessageBox.Show("This Tbot copy is already running. Use a second extracted folder for the second copy.",
                "Tbot Ultra", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(3);
            return;
        }

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        _serviceProvider = ConfigureServices();
        Services = _serviceProvider;

        // Fallback: dark OS title bar for any window that isn't wired through ThemeChrome in its
        // constructor. Windows apply it earlier (at SourceInitialized) via ThemeChrome to avoid the
        // brief light-title-bar flash; this Loaded hook just guarantees nothing is left light.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded));

        base.OnStartup(e);

        // The development VBS already owns its launch progress window. The portable EXE has no
        // launcher, so it shows the same visual during app initialization only.
        if (e.Args.Contains("--dev-launcher", StringComparer.OrdinalIgnoreCase))
        {
            StartMainWindow(null);
            return;
        }

        _startupProgressWindow = new StartupProgressWindow();
        _startupProgressWindow.CancelRequested += (_, _) =>
        {
            _startupCanceled = true;
            Shutdown(2);
        };
        MainWindow = _startupProgressWindow;
        _startupProgressWindow.Show();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => StartMainWindow(_startupProgressWindow)));
    }

    private void StartMainWindow(StartupProgressWindow? progress)
    {
        if (_startupCanceled)
        {
            return;
        }

        progress?.SetProgress(24, "Loading settings and dashboard...");
        try
        {
            var window = new TbotUltra.Desktop.MainWindow();
            if (_startupCanceled)
            {
                window.Close();
                return;
            }

            MainWindow = window;
            if (progress is not null)
            {
                progress.SetProgress(90, "Opening Tbot Ultra...");
                window.ContentRendered += (_, _) =>
                {
                    progress.CloseAfterMainWindowReady();
                    _startupProgressWindow = null;
                };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            TryWriteUnhandledException("Startup", ex);
            if (progress is null)
            {
                Shutdown(-1);
                return;
            }

            var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "desktop-unhandled.log");
            progress.ShowFailure("Tbot Ultra could not start.", ex.Message, logPath);
        }
    }

    private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            if (window is TbotUltra.Desktop.MainWindow)
            {
                _hasLoadedMainWindow = true;
            }
            ThemeChrome.TryEnableDarkTitleBar(window);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        _serviceProvider?.Dispose();
        _serviceProvider = null;
        _copyInstanceMutex?.ReleaseMutex();
        _copyInstanceMutex?.Dispose();
        _copyInstanceMutex = null;

        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // View models. Keep shared panel view models in the app provider until
        // constructor injection is introduced for MainWindow and child panels.
        // Services that MainWindow currently new()s in its constructor (BotConfigStore,
        // DesktopBotService, etc.) will move here in follow-up commits as their owners
        // are migrated to constructor injection.
        services.AddSingleton<HeroViewModel>();
        services.AddSingleton<InboxViewModel>();
        services.AddSingleton<TroopTrainingViewModel>();
        services.AddSingleton<ResourcesViewModel>();
        services.AddSingleton<BuildingsViewModel>();
        services.AddSingleton<ResourceTransferViewModel>();
        services.AddSingleton<ReinforcementViewModel>();
        services.AddSingleton<FarmListsViewModel>();
        services.AddSingleton<TravianQueueViewModel>();
        services.AddSingleton<AutomationLoopViewModel>();
        services.AddSingleton<DashboardProjectionService>();
        services.AddSingleton<AlarmsViewModel>();
        services.AddSingleton<TerminalViewModel>();

        // Orchestration. LoopController owns the queue-auto-run gate and the
        // is-closing flag; subsequent commits will fold the continuous-loop
        // CancellationTokenSources into it as well.
        services.AddSingleton<LoopController>();

        return services.BuildServiceProvider();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteUnhandledException("DispatcherUnhandledException", e.Exception);
        e.Handled = true;

        // StartupUri construction failures happen before MainWindow loads. Keeping the
        // exception handled without an explicit shutdown leaves an invisible dispatcher
        // process running indefinitely.
        if (!_hasLoadedMainWindow)
        {
            if (_startupProgressWindow is not null)
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "desktop-unhandled.log");
                _startupProgressWindow.ShowFailure("Tbot Ultra could not start.", e.Exception.Message, logPath);
            }
            else
            {
                Shutdown(-1);
            }
        }
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        TryWriteUnhandledException("AppDomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteUnhandledException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void TryWriteUnhandledException(string source, Exception? exception)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "desktop-unhandled.log");
            var text = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .Append(source).AppendLine()
                .AppendLine(exception?.ToString() ?? "No exception object.")
                .AppendLine(new string('-', 80))
                .ToString();
            File.AppendAllText(logPath, text);
        }
        catch
        {
            // Last-resort logger must never throw.
        }
    }
}

