using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TbotUltra.Desktop;

// Freeze is an in-memory safety lock. Unlike session sleep, it keeps the current browser and
// authentication state untouched; it only cancels Tbot-owned work and prevents new Tbot actions.
public partial class MainWindow
{
    private volatile bool _freezeActive;
    private readonly List<TextBlock> _freezeSnowflakes = [];
    private readonly List<FreezeSnowParticle> _freezeSnowParticles = [];

    private sealed record FreezeSnowParticle(TextBlock View, double StartX, double StartY);

    private bool IsFreezeActive => _freezeActive;

    private void ActivateFreeze()
    {
        if (IsFreezeActive)
        {
            return;
        }

        var choice = AppDialog.ShowCustom(
            this,
            "Sleep mode will activate and lock Tbot Ultra. Pending program actions will be cancelled, but the current browser tab and session remain unchanged.",
            "Activate freeze?",
            new (string, MessageBoxResult)[]
            {
                ("Activate freeze", MessageBoxResult.Yes),
                ("Cancel", MessageBoxResult.Cancel),
            },
            MessageBoxImage.Information,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            infoResult: MessageBoxResult.Yes);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        _freezeActive = true;
        _restartContinuousLoopAfterStop = false;
        _startContinuousLoopAfterQueueStop = false;
        _inboxAutoEnabled = false;
        _inboxRefreshTimer.Stop();
        _resourceSnapshotRefreshTimer.Stop();
        EndInlineWait();

        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
        _loopController.CancelVillageSwitch();
        _loopController.CancelSessionScope();

        var recovered = _botService.ResetOrphanedRunningQueueItems();
        if (recovered > 0)
        {
            AppendLog($"[freeze] returned {recovered} interrupted queue item(s) to Pending.");
        }

        StartFreezeSnowAnimation();
        FreezeOverlay.Visibility = Visibility.Visible;
        ToggleUiBusy(false);
        UpdateExecutionStateIndicator();
        AppendLog("[freeze] active. Tbot work and Travian traffic are blocked; browser session kept open.");
    }

    private void LeaveFreezeButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!IsFreezeActive)
        {
            return;
        }

        _freezeActive = false;
        FreezeOverlay.Visibility = Visibility.Collapsed;
        StopFreezeSnowAnimation();
        ToggleUiBusy(false);
        UpdateExecutionStateIndicator();
        AppendLog("[freeze] left. Browser/session unchanged; bot remains stopped until started manually.");
    }

    private void StartFreezeSnowAnimation()
    {
        if (_freezeSnowflakes.Count == 0)
        {
            for (var index = 0; index < 84; index++)
            {
                var snowflake = new TextBlock
                {
                    Text = index % 5 == 0 ? "❅" : "·",
                    FontSize = 12 + index % 8,
                    Foreground = (Brush)FindResource("InfoTextBrush"),
                };
                var startX = 18 + index * 43 % 1120;
                var startY = 12 + index * 71 % 650;
                Canvas.SetLeft(snowflake, startX);
                Canvas.SetTop(snowflake, startY);
                FreezeSnowCanvas.Children.Add(snowflake);
                _freezeSnowflakes.Add(snowflake);
                _freezeSnowParticles.Add(new FreezeSnowParticle(
                    snowflake,
                    startX,
                    startY));
            }
        }

        foreach (var particle in _freezeSnowParticles)
        {
            Canvas.SetLeft(particle.View, particle.StartX);
            Canvas.SetTop(particle.View, particle.StartY);
        }
    }

    private void StopFreezeSnowAnimation()
    {
        // Snow is intentionally static while frozen so the overlay adds no recurring render work.
    }
}
