using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TbotUltra.Desktop.Services.Orchestration;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static ServiceProvider? _serviceProvider;

    /// <summary>
    /// Application-wide service provider. Populated in <see cref="OnStartup"/>
    /// before the StartupUri creates MainWindow. Use this from code that needs
    /// to resolve services that are not yet wired through constructor injection.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
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
    }

    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
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

