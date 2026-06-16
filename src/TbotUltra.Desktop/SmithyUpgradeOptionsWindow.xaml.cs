using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

/// <summary>One troop offered in the Smithy upgrade-options popup (input to the window).</summary>
public sealed record SmithyTroopOption(string Key, string Name, bool Enabled, int TargetLevel);

/// <summary>
/// Lets the user choose which troops the Smithy task improves and to which level. The caller seeds the
/// rows from the tribe troop catalog merged with the saved selection; on Save the window exposes the
/// enabled troops via <see cref="Result"/> for the caller to persist + queue.
/// </summary>
public partial class SmithyUpgradeOptionsWindow : Window
{
    public const int MaxLevel = 20;

    private readonly ObservableCollection<TroopRow> _rows = new();
    private readonly string _villageName;

    public IReadOnlyList<SmithyUpgradeSelection> Result { get; private set; } = [];

    // True when the user chose "Sync to all villages": the caller applies Result to every village.
    public bool SyncRequested { get; private set; }

    public SmithyUpgradeOptionsWindow(IReadOnlyList<SmithyTroopOption> troops, string villageName)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        _villageName = string.IsNullOrWhiteSpace(villageName) ? "this village" : villageName.Trim();
        SubtitleTextBlock.Text = $"Selection for village: {_villageName}";

        foreach (var troop in troops ?? [])
        {
            _rows.Add(new TroopRow(troop.Key, troop.Name, troop.Enabled, troop.TargetLevel));
        }

        TroopItemsControl.ItemsSource = _rows;
    }

    private IReadOnlyList<SmithyUpgradeSelection> BuildSelection()
    {
        return _rows
            .Where(row => row.IsEnabled)
            .Select(row => new SmithyUpgradeSelection(row.Key, row.Name, row.TargetLevel))
            .ToList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildSelection();
        DialogResult = true;
        Close();
    }

    private void SyncToAllButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppDialog.Show(
            this,
            $"Copy this Smithy upgrade selection from '{_villageName}' to ALL villages?\n\n"
            + "This overwrites every village's selected troops and target levels.",
            "Sync to all villages",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        Result = BuildSelection();
        SyncRequested = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // Row view-model. INotifyPropertyChanged is needed so the level ComboBox enables/disables live when
    // the troop checkbox is toggled.
    private sealed class TroopRow : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private int _targetLevel;

        public TroopRow(string key, string name, bool isEnabled, int targetLevel)
        {
            Key = key;
            Name = name;
            _isEnabled = isEnabled;
            _targetLevel = targetLevel < 1 || targetLevel > MaxLevel ? MaxLevel : targetLevel;
        }

        public string Key { get; }

        public string Name { get; }

        public IReadOnlyList<int> LevelOptions { get; } = Enumerable.Range(1, MaxLevel).ToList();

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public int TargetLevel
        {
            get => _targetLevel;
            set
            {
                if (_targetLevel == value)
                {
                    return;
                }

                _targetLevel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetLevel)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
