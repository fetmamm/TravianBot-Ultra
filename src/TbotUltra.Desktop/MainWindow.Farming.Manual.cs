using System;
namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private void StartManualFarmingButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        AppendLog("[farm-manual] Start manual farming is disabled for now.");
    }

    private void SetFarmingOperationBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingOperationBusy(busy));
            return;
        }

        if (busy)
        {
            EnsureManualExecutionTracking();
        }

        _farmingOperationBusy = busy;
        try
        {
            SyncFarmingControlsEnabledState();
            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!busy)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void SetFarmingFunctionRunning(bool running, bool showCancelButton = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetFarmingFunctionRunning(running, showCancelButton));
            return;
        }

        try
        {
            if (running)
            {
                EnsureManualExecutionTracking();
            }

            if (CancelFarmingOperationButton is not null)
            {
                var showButton = running && showCancelButton;
                CancelFarmingOperationButton.Visibility = showButton
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
                CancelFarmingOperationButton.IsEnabled = showButton;
            }

            UpdateExecutionStateIndicator();
        }
        finally
        {
            if (!running)
            {
                CompleteManualExecutionTrackingIfNeeded();
            }
        }
    }

    private void CancelFarmingOperationButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CancelFarmingOperationButton is not null)
        {
            CancelFarmingOperationButton.IsEnabled = false;
        }

        AppendLog("Cancel requested for the running farming operation.");
        _loopController.CancelOperation();
    }

    private void UpdateManualFarmingRunningState()
    {
        if (StartManualFarmingButton is not null)
        {
            StartManualFarmingButton.Content = "Start manual farming";
            StartManualFarmingButton.IsEnabled = false;
        }
    }

    private void UpdateManualFarmingExecutionCounter()
    {
        if (ManualFarmingExecutionCountTextBlock is null)
        {
            return;
        }

        ManualFarmingExecutionCountTextBlock.Text = _manualFarmSessionExecutionCount.ToString("N0");
    }
}
