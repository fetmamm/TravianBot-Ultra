using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

public sealed record HeroResourceOverviewResult(
    string VillageKey,
    string VillageName,
    VillageSettingsStore.HeroResourceSettings Settings);

public sealed class HeroResourceOverviewRow : INotifyPropertyChanged
{
    private bool _isHeroResourcesEnabled;
    private bool _useConstruction;
    private bool _useSmithy;
    private bool _useBrewery;
    private bool _useTownHall;

    public HeroResourceOverviewRow(
        string villageKey,
        string villageName,
        VillageSettingsStore.HeroResourceSettings settings)
    {
        VillageKey = villageKey;
        VillageName = villageName;
        _isHeroResourcesEnabled = settings.IsEnabled;
        _useConstruction = settings.UseConstruction;
        _useSmithy = settings.UseSmithy;
        _useBrewery = settings.UseBrewery;
        _useTownHall = settings.UseTownHall;
    }

    public string VillageKey { get; }
    public string VillageName { get; }

    public bool IsHeroResourcesEnabled
    {
        get => _isHeroResourcesEnabled;
        set => SetProperty(ref _isHeroResourcesEnabled, value);
    }

    public bool UseConstruction
    {
        get => _useConstruction;
        set => SetProperty(ref _useConstruction, value);
    }

    public bool UseSmithy
    {
        get => _useSmithy;
        set => SetProperty(ref _useSmithy, value);
    }

    public bool UseBrewery
    {
        get => _useBrewery;
        set => SetProperty(ref _useBrewery, value);
    }

    public bool UseTownHall
    {
        get => _useTownHall;
        set => SetProperty(ref _useTownHall, value);
    }

    public VillageSettingsStore.HeroResourceSettings Settings => new(
        IsHeroResourcesEnabled,
        UseConstruction,
        UseSmithy,
        UseBrewery,
        UseTownHall);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Settings)));
    }
}

public partial class HeroResourceOverviewWindow : Window
{
    public ObservableCollection<HeroResourceOverviewRow> Rows { get; }

    public IReadOnlyList<HeroResourceOverviewResult> Results { get; private set; } =
        Array.Empty<HeroResourceOverviewResult>();

    public HeroResourceOverviewWindow(IReadOnlyList<HeroResourceOverviewRow> rows)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);

        Rows = new ObservableCollection<HeroResourceOverviewRow>(rows);
        DataContext = this;
        SubtitleTextBlock.Text = $"{Rows.Count} village(s)";
    }

    private IReadOnlyList<HeroResourceOverviewResult> BuildResults()
    {
        return Rows
            .Select(row => new HeroResourceOverviewResult(
                row.VillageKey,
                row.VillageName,
                row.Settings))
            .ToList();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Results = BuildResults();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
