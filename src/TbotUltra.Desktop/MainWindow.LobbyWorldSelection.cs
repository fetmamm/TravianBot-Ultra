using System.Windows;
using TbotUltra.Desktop.Views;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private Task SaveResolvedLobbyWorldServerAsync(
        LobbyWorldServerResolution resolution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        void SaveAndRefresh()
        {
            _accountStore.UpdateAccountServer(
                resolution.AccountName,
                resolution.WorldName,
                resolution.ServerUrl);
            SyncServerFromActiveAccount();
            RefreshAccountPicker();
            UpdateAccountInfoLabel(resolution.AccountName);
            AppendLog(
                $"[lobby-login] Manage account '{resolution.AccountName}' updated to '{resolution.WorldName}' ({resolution.ServerUrl}).");
        }

        if (Dispatcher.CheckAccess())
        {
            SaveAndRefresh();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(SaveAndRefresh).Task.WaitAsync(cancellationToken);
    }

    private Task<string?> SelectLobbyWorldAsync(
        LobbyWorldSelectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowLobbyWorldSelection(request));
        }

        return Dispatcher
            .InvokeAsync(() => ShowLobbyWorldSelection(request))
            .Task
            .WaitAsync(cancellationToken);
    }

    private string? ShowLobbyWorldSelection(LobbyWorldSelectionRequest request)
    {
        var content = new LobbyWorldSelectionView(request);

        var result = AppDialog.ShowCustomContent(
            this,
            content,
            "Choose Travian world",
            [("Select world", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Question,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            successResult: MessageBoxResult.Yes,
            width: 620);
        return result == MessageBoxResult.Yes ? content.SelectedWorldUid : null;
    }
}
