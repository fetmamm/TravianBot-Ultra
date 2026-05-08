using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TbotUltra.Desktop.ViewModels;

namespace TbotUltra.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
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

        Services = ConfigureServices();

        base.OnStartup(e);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // View models. Transient so each window/control gets its own instance.
        // Services that MainWindow currently new()s in its constructor (BotConfigStore,
        // DesktopBotService, etc.) will move here in follow-up commits as their owners
        // are migrated to constructor injection.
        services.AddTransient<MainViewModel>();

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

