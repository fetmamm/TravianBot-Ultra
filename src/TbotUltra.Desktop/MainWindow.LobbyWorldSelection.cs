using System.Windows;
using System.Windows.Controls;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
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
        var choices = request.Worlds
            .Select(world => new LobbyWorldChoice(
                world.WorldUid,
                world.DisplayText))
            .ToList();
        var list = new ListBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(LobbyWorldChoice.DisplayText),
            MinHeight = 90,
            MaxHeight = 260,
            Margin = new Thickness(0, 10, 0, 0),
            SelectedIndex = choices.Count > 0 ? 0 : -1,
        };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = request.PreviousSelectionFailed
                ? $"The selected world did not reach {request.ConfiguredServerUrl}. Choose another owned world. " +
                  "The selection is remembered only after a successful login."
                : $"Tbot Ultra could not automatically match '{request.ConfiguredServerName}' ({request.ConfiguredServerUrl}) to an owned lobby world. " +
                  "Choose the matching world. The selection is remembered after a successful login.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(list);

        var result = AppDialog.ShowCustomContent(
            this,
            content,
            "Choose Travian world",
            [("Select world", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxImage.Question,
            MessageBoxResult.Yes,
            MessageBoxResult.Cancel,
            accentResult: MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes && list.SelectedItem is LobbyWorldChoice selected
            ? selected.WorldUid
            : null;
    }

    private sealed record LobbyWorldChoice(string WorldUid, string DisplayText);
}
