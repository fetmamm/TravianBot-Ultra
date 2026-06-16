using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TbotUltra.Core.Travian;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class CatapultWaveWindow : Window
{
    private readonly IReadOnlyList<string> _troopTypes;
    private readonly Dictionary<string, long> _availableTroops;
    private int? _rallyPointLevel;
    private readonly Dictionary<string, TextBox> _firstAttackInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> _waveInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Run> _firstAttackAmountRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Hyperlink> _firstAttackAmountLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Run> _waveAmountRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _windowCts = new();
    private bool _suppressRefresh;
    private bool _isRunning;
    private bool _isRefreshing;

    public Func<CatapultWaveRequest, Action<string>, CancellationToken, Task<CatapultWaveRunResult>>? StartRequested { get; init; }
    public Func<Action<string>, CancellationToken, Task<CatapultWaveSetupInfo>>? RefreshRequested { get; init; }

    /// <summary>
    /// Optional first-time load run automatically when the window opens. While it runs the busy
    /// overlay is shown so the popup never appears empty/unresponsive. If null, the window opens
    /// with whatever troops were passed to the constructor.
    /// </summary>
    public Func<Action<string>, CancellationToken, Task<CatapultWaveSetupInfo>>? InitialLoadRequested { get; init; }

    public CatapultWaveWindow(string tribe, IReadOnlyDictionary<string, long>? availableTroops = null, int? rallyPointLevel = null)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _troopTypes = TroopCatalog.ResolveTroopTypesForTribe(tribe);
        _availableTroops = availableTroops is null
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(availableTroops, StringComparer.OrdinalIgnoreCase);
        _rallyPointLevel = rallyPointLevel;
        ConfigureZeroDefaultTextBox(XTextBox);
        ConfigureZeroDefaultTextBox(YTextBox);
        BuildTroopGrid(FirstAttackTroopsGrid, _firstAttackInputs, isFirstAttackGrid: true);
        BuildTroopGrid(WaveTroopsGrid, _waveInputs, isFirstAttackGrid: false);
        RefreshRallyPointLevelText();
        RefreshUiState();
        Loaded += OnWindowLoaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnWindowLoaded;
        _windowCts.Cancel();
        _windowCts.Dispose();
        base.OnClosed(e);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Only run the auto-load once.
        Loaded -= OnWindowLoaded;

        if (InitialLoadRequested is null)
        {
            // Nothing to load — the overlay starts visible (XAML IsBusy=True), so hide it.
            BusyOverlay.Hide();
            return;
        }

        SetRefreshing(true);
        BusyOverlay.Show("Catapult waves", "Reading troops from Rally Point…");
        try
        {
            var setupInfo = await InitialLoadRequested(message => SetStatus(message, isAlarm: false), _windowCts.Token);
            UpdateSetupInfo(setupInfo);
            SetStatus("Troops loaded from Rally Point.", isAlarm: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loading canceled.", isAlarm: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isAlarm: true);
        }
        finally
        {
            BusyOverlay.Hide();
            SetRefreshing(false);
        }
    }

    #region UI building

    private void BuildTroopGrid(
        Grid grid,
        Dictionary<string, TextBox> inputs,
        bool isFirstAttackGrid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        for (var i = 0; i < _troopTypes.Count; i++)
        {
            var troopType = _troopTypes[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = troopType,
                Margin = new Thickness(0, i == 0 ? 0 : 6, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ThemeColors.Get("TextPrimaryBrush")),
            };

            label.Inlines.Clear();
            label.Inlines.Add(new Run(troopType));
            label.Inlines.Add(new Run(" "));

            var amountRun = new Run("(0)");
            if (isFirstAttackGrid)
            {
                var amountLink = new Hyperlink(amountRun)
                {
                    Cursor = Cursors.Hand,
                    TextDecorations = null,
                    ToolTip = "Click to fill first attack with the remaining available troops after waves.",
                };
                amountLink.Click += (_, _) => FillFirstAttackWithRemaining(troopType);
                label.Inlines.Add(amountLink);
                _firstAttackAmountRuns[troopType] = amountRun;
                _firstAttackAmountLinks[troopType] = amountLink;
            }
            else
            {
                label.Inlines.Add(amountRun);
                _waveAmountRuns[troopType] = amountRun;
            }

            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var input = new TextBox
            {
                Text = "0",
                Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0),
            };
            ConfigureZeroDefaultTextBox(input);
            input.TextChanged += Input_TextChanged;
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            inputs[troopType] = input;
        }
    }

    #endregion

    #region Start flow

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isRefreshing)
        {
            return;
        }

        if (!TryBuildRequest(out var request, out var error))
        {
            SetStatus(error, isAlarm: true);
            return;
        }

        if (StartRequested is null)
        {
            SetStatus("Catapult wave runner is not connected.", isAlarm: true);
            return;
        }

        var confirm = ShowStartConfirmation(request!);
        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Catapult waves canceled.", isAlarm: true);
            return;
        }

        SetRunning(true);
        BusyOverlay.Show("Sending catapult waves", "Preparing catapult waves…");
        try
        {
            SetStatus("Preparing catapult waves...", isAlarm: false);
            var result = await StartRequested(request!, message => SetStatus(message, isAlarm: false), _windowCts.Token);
            var attackMode = request!.RaidAttack ? "raid" : "normal attack";
            var done = $"Sent {result.SentCount}/{result.TotalAttacks} {attackMode}(s) to ({result.X}|{result.Y}).";
            SetStatus(done, isAlarm: false);
            AppDialog.Show(this, done, "Catapult waves", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Catapult waves canceled.", isAlarm: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isAlarm: true);
        }
        finally
        {
            BusyOverlay.Hide();
            SetRunning(false);
        }
    }

    #endregion

    #region Confirmation popup

    private MessageBoxResult ShowStartConfirmation(CatapultWaveRequest request)
    {
        var mode = request.RaidAttack ? "Raid" : "Normal attack";
        var content = new StackPanel
        {
            Width = 360,
        };

        content.Children.Add(new TextBlock
        {
            Text = "Start catapult waves?",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ThemeColors.Get("TextPrimaryBrush")),
            Margin = new Thickness(0, 0, 0, 10),
        });
        content.Children.Add(CreateSummaryText($"Target: ({request.X}|{request.Y})"));
        content.Children.Add(CreateSummaryText($"Mode: {mode}"));
        content.Children.Add(CreateSummaryText($"First attack: {FormatTroopSet(request.FirstAttackTroops)}"));
        content.Children.Add(CreateSummaryText($"Waves: {request.WaveCount}"));
        content.Children.Add(CreateSummaryText($"Each wave: {FormatTroopSet(request.WaveTroops)}"));
        content.Children.Add(CreateSummaryText($"Total attacks: {request.WaveCount + 1}"));

        return AppDialog.ShowContent(
            this,
            content,
            "Confirm catapult waves",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
    }

    private static TextBlock CreateSummaryText(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ThemeColors.Get("TextPrimaryBrush")),
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private static string FormatTroopSet(IReadOnlyDictionary<string, int> troops)
    {
        var parts = troops
            .Where(pair => pair.Value > 0)
            .Select(pair => $"{pair.Key}: {FormatLargeCount(pair.Value)}")
            .ToList();

        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }

    #endregion

    #region UI events

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            SetStatus("Catapult waves are running. Wait until the operation finishes.", isAlarm: true);
            return;
        }

        DialogResult = false;
        Close();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressRefresh)
        {
            return;
        }

        if (sender is TextBox input)
        {
            UpdateZeroDefaultTextBoxForeground(input);
        }

        RefreshUiState();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _isRefreshing)
        {
            return;
        }

        if (RefreshRequested is null)
        {
            SetStatus("Troop refresh is not connected.", isAlarm: true);
            return;
        }

        SetRefreshing(true);
        BusyOverlay.Show("Catapult waves", "Refreshing troops from Rally Point…");
        try
        {
            SetStatus("Refreshing troops from Rally Point...", isAlarm: false);
            var setupInfo = await RefreshRequested(message => SetStatus(message, isAlarm: false), _windowCts.Token);
            UpdateSetupInfo(setupInfo);
            SetStatus("Troops refreshed from Rally Point.", isAlarm: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Troop refresh canceled.", isAlarm: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isAlarm: true);
        }
        finally
        {
            BusyOverlay.Hide();
            SetRefreshing(false);
        }
    }

    #endregion

    #region Live calculations

    private void FillFirstAttackWithRemaining(string troopType)
    {
        if (!_firstAttackInputs.TryGetValue(troopType, out var input))
        {
            return;
        }

        input.Text = ResolveFirstAttackRemaining(troopType).ToString();
        UpdateZeroDefaultTextBoxForeground(input);
        RefreshUiState();
    }

    private void UpdateSetupInfo(CatapultWaveSetupInfo setupInfo)
    {
        _availableTroops.Clear();
        foreach (var pair in setupInfo.AvailableTroops)
        {
            _availableTroops[pair.Key] = Math.Max(0, pair.Value);
        }

        _rallyPointLevel = setupInfo.RallyPointLevel;
        RefreshRallyPointLevelText();
        RefreshUiState();
    }

    private void RefreshUiState()
    {
        if (StartButton is null || StatusTextBlock is null)
        {
            return;
        }

        NormalizeTroopAmounts();
        RefreshTroopLabels();
        if (!_isRunning)
        {
            StartButton.IsEnabled = true;
        }
    }

    private void RefreshRallyPointLevelText()
    {
        RallyPointLevelTextBlock.Inlines.Clear();
        RallyPointLevelTextBlock.Foreground = new SolidColorBrush(ThemeColors.Get("TextMutedBrush"));
        RallyPointLevelTextBlock.Inlines.Add(new Run("Rallypoint level: "));
        RallyPointLevelTextBlock.Inlines.Add(new Run(_rallyPointLevel?.ToString() ?? "-")
        {
            Foreground = new SolidColorBrush(_rallyPointLevel == 20
                ? ThemeColors.Get("AmberBrush")
                : ThemeColors.Get("TextMutedBrush")),
        });
    }

    private void NormalizeTroopAmounts()
    {
        if (!int.TryParse(WaveCountTextBox.Text.Trim(), out var waveCount) || waveCount <= 0)
        {
            return;
        }

        _suppressRefresh = true;
        try
        {
            foreach (var troopType in _troopTypes)
            {
                var available = ResolveAvailable(troopType);
                var wave = ReadInputValue(_waveInputs, troopType);
                var maxWave = (int)Math.Min(int.MaxValue, available / waveCount);
                if (wave > maxWave)
                {
                    SetInputValue(_waveInputs, troopType, maxWave);
                    wave = maxWave;
                }

                var firstMax = Math.Max(0, available - (long)wave * waveCount);
                var first = ReadInputValue(_firstAttackInputs, troopType);
                if (first > firstMax)
                {
                    SetInputValue(_firstAttackInputs, troopType, (int)Math.Min(int.MaxValue, firstMax));
                }
            }
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private void RefreshTroopLabels()
    {
        foreach (var troopType in _troopTypes)
        {
            if (_firstAttackAmountRuns.TryGetValue(troopType, out var firstAmountRun))
            {
                var remaining = ResolveFirstAttackRemaining(troopType);
                var current = ReadInputValue(_firstAttackInputs, troopType);
                var canFillMore = remaining > current;
                firstAmountRun.Text = $"({FormatLargeCount(remaining)})";
                firstAmountRun.Foreground = new SolidColorBrush(canFillMore
                    ? ThemeColors.Get("EmeraldBrush")
                    : ThemeColors.Get("TextSubtleBrush"));
                firstAmountRun.FontWeight = remaining > 0 ? FontWeights.SemiBold : FontWeights.Normal;

                if (_firstAttackAmountLinks.TryGetValue(troopType, out var link))
                {
                    link.Cursor = canFillMore ? Cursors.Hand : Cursors.Arrow;
                    link.Foreground = firstAmountRun.Foreground;
                }
            }

            if (_waveAmountRuns.TryGetValue(troopType, out var waveAmountRun))
            {
                var waveTotal = ResolveWaveTotal(troopType);
                var hasWaveTroops = waveTotal > 0;
                waveAmountRun.Text = $"({FormatLargeCount(waveTotal)})";
                waveAmountRun.Foreground = new SolidColorBrush(hasWaveTroops
                    ? ThemeColors.Get("EmeraldBrush")
                    : ThemeColors.Get("TextSubtleBrush"));
                waveAmountRun.FontWeight = hasWaveTroops ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    #endregion

    #region Request validation

    private bool TryBuildRequest(out CatapultWaveRequest? request, out string error)
    {
        request = null;
        error = string.Empty;

        if (!int.TryParse(XTextBox.Text.Trim(), out var x) || x is < -400 or > 400)
        {
            error = "ALARM: X must be an integer between -400 and 400.";
            return false;
        }

        if (!int.TryParse(YTextBox.Text.Trim(), out var y) || y is < -400 or > 400)
        {
            error = "ALARM: Y must be an integer between -400 and 400.";
            return false;
        }

        if (!int.TryParse(WaveCountTextBox.Text.Trim(), out var waveCount) || waveCount < 0 || waveCount > CatapultWaveLimits.MaxWaveCount)
        {
            error = $"ALARM: Waves must be an integer between 0 and {CatapultWaveLimits.MaxWaveCount}.";
            return false;
        }

        if (!TryReadTroops(_firstAttackInputs, out var firstTroops, out error))
        {
            error = $"ALARM: {error}";
            return false;
        }

        Dictionary<string, int> waveTroops = new(StringComparer.OrdinalIgnoreCase);
        if (waveCount > 0 && !TryReadTroops(_waveInputs, out waveTroops, out error))
        {
            error = $"ALARM: {error}";
            return false;
        }

        if (firstTroops.Count == 0)
        {
            error = "ALARM: First attack must include at least one troop.";
            return false;
        }

        if (waveCount > 0 && waveTroops.Count == 0)
        {
            error = "ALARM: Waves must include at least one troop.";
            return false;
        }

        foreach (var troopType in firstTroops.Keys.Concat(waveTroops.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var first = firstTroops.TryGetValue(troopType, out var firstValue) ? firstValue : 0;
            var wave = waveTroops.TryGetValue(troopType, out var waveValue) ? waveValue : 0;
            var required = (long)first + (long)wave * waveCount;
            var available = ResolveAvailable(troopType);
            if (required > available)
            {
                error = $"ALARM: Not enough {troopType}. Available: {FormatLargeCount(available)}, required: {FormatLargeCount(required)}.";
                return false;
            }
        }

        request = new CatapultWaveRequest(
            x,
            y,
            waveCount,
            RaidAttackRadioButton.IsChecked == true,
            firstTroops,
            waveTroops,
            null,
            null);
        return true;
    }

    private static bool TryReadTroops(
        IReadOnlyDictionary<string, TextBox> inputs,
        out Dictionary<string, int> troops,
        out string error)
    {
        troops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        foreach (var input in inputs)
        {
            var raw = input.Value.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = "0";
            }

            if (!int.TryParse(raw, out var value) || value < 0)
            {
                error = $"{input.Key} amount must be 0 or greater.";
                return false;
            }

            if (value > 0)
            {
                troops[input.Key] = value;
            }
        }

        return true;
    }

    #endregion

    #region Calculation helpers

    private long ResolveFirstAttackRemaining(string troopType)
    {
        if (!int.TryParse(WaveCountTextBox.Text.Trim(), out var waveCount) || waveCount <= 0)
        {
            return ResolveAvailable(troopType);
        }

        var wave = ReadInputValue(_waveInputs, troopType);
        return Math.Max(0, ResolveAvailable(troopType) - (long)wave * waveCount);
    }

    private long ResolveWaveTotal(string troopType)
    {
        if (!int.TryParse(WaveCountTextBox.Text.Trim(), out var waveCount) || waveCount <= 0)
        {
            return 0;
        }

        var wave = ReadInputValue(_waveInputs, troopType);
        return Math.Max(0, (long)wave * waveCount);
    }

    private long ResolveAvailable(string troopType)
    {
        return _availableTroops.TryGetValue(troopType, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static int ReadInputValue(IReadOnlyDictionary<string, TextBox> inputs, string troopType)
    {
        return inputs.TryGetValue(troopType, out var input) && int.TryParse(input.Text.Trim(), out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private void SetInputValue(IReadOnlyDictionary<string, TextBox> inputs, string troopType, int value)
    {
        if (inputs.TryGetValue(troopType, out var input))
        {
            input.Text = Math.Max(0, value).ToString();
            UpdateZeroDefaultTextBoxForeground(input);
        }
    }

    #endregion

    #region Zero-placeholder text boxes

    private void ConfigureZeroDefaultTextBox(TextBox input)
    {
        input.GotKeyboardFocus += ZeroDefaultTextBox_GotKeyboardFocus;
        input.LostKeyboardFocus += ZeroDefaultTextBox_LostKeyboardFocus;
        UpdateZeroDefaultTextBoxForeground(input);
    }

    private void ZeroDefaultTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox input)
        {
            return;
        }

        if (input.Text.Trim() == "0")
        {
            input.Clear();
        }

        UpdateZeroDefaultTextBoxForeground(input);
    }

    private void ZeroDefaultTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox input)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input.Text))
        {
            input.Text = "0";
        }

        UpdateZeroDefaultTextBoxForeground(input);
    }

    private static void UpdateZeroDefaultTextBoxForeground(TextBox input)
    {
        input.Foreground = !input.IsKeyboardFocusWithin && input.Text.Trim() == "0"
            ? new SolidColorBrush(ThemeColors.Get("BorderMutedBrush"))
            : new SolidColorBrush(ThemeColors.Get("TextPrimaryBrush"));
    }

    #endregion

    #region Run state

    private void SetRunning(bool running)
    {
        _isRunning = running;
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = !running;
        RefreshButton.IsEnabled = !running && !_isRefreshing;
        XTextBox.IsEnabled = !running;
        YTextBox.IsEnabled = !running;
        WaveCountTextBox.IsEnabled = !running;
        NormalAttackRadioButton.IsEnabled = !running;
        RaidAttackRadioButton.IsEnabled = !running;
        FirstAttackTroopsGrid.IsEnabled = !running;
        WaveTroopsGrid.IsEnabled = !running;
    }

    private void SetRefreshing(bool refreshing)
    {
        _isRefreshing = refreshing;
        RefreshButton.IsEnabled = !refreshing && !_isRunning;
        StartButton.IsEnabled = !refreshing && !_isRunning;
        CancelButton.IsEnabled = !refreshing && !_isRunning;
    }

    private void SetStatus(string status, bool isAlarm)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetStatus(status, isAlarm));
            return;
        }

        StatusTextBlock.Text = status;
        StatusTextBlock.Foreground = isAlarm
            ? new SolidColorBrush(ThemeColors.Get("DangerTextBrush"))
            : new SolidColorBrush(ThemeColors.Get("TextSecondaryBrush"));

        // Mirror live progress into the busy overlay so the user sees what's happening there too.
        if (!isAlarm && BusyOverlay.IsBusy)
        {
            BusyOverlay.Text = status;
        }
    }

    private static string FormatLargeCount(long value)
    {
        return Math.Max(0, value).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
    }

    #endregion
}
