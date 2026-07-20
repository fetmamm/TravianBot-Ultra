using System.Text.RegularExpressions;
using System.Windows.Controls;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Views;

public partial class LobbyWorldSelectionView : UserControl
{
    public LobbyWorldSelectionView(LobbyWorldSelectionRequest request)
    {
        InitializeComponent();
        Worlds = request.Worlds
            .Select(world => new LobbyWorldCard(
                world.WorldUid,
                string.IsNullOrWhiteSpace(world.Name) ? "Unnamed world" : world.Name.Trim(),
                CleanDetails(world.Details)))
            .ToList();
        DataContext = this;
    }

    public IReadOnlyList<LobbyWorldCard> Worlds { get; }

    public string? SelectedWorldUid
        => (WorldListBox.SelectedItem as LobbyWorldCard)?.WorldUid;

    private static string CleanDetails(string details)
    {
        var cleaned = Regex.Replace(details ?? string.Empty, @"\bplay\s+now\b", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '·', '-', '|');
        return cleaned.Length == 0 ? "Owned lobby world" : cleaned;
    }

    public sealed record LobbyWorldCard(string WorldUid, string Name, string Details);
}
