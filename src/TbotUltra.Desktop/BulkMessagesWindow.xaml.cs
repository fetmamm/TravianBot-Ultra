using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop;

public partial class BulkMessagesWindow : Window
{
    private readonly Func<BulkMessageAnalyzeRequest, IProgress<BulkMessageProgress>, CancellationToken, Task<BulkMessageAnalyzeResult>> _analyzer;
    private readonly Func<BulkMessageRequest, IProgress<BulkMessageProgress>, CancellationToken, Task<BulkMessageSendResult>> _sender;
    private readonly Func<CancellationToken, Task> _clearCache;
    private readonly CancellationToken _externalToken;
    private CancellationTokenSource? _operationCts;
    private bool _busy;
    private BulkMessageAnalyzeResult? _lastAnalyzeResult;

    public BulkMessagesWindow(
        Func<BulkMessageAnalyzeRequest, IProgress<BulkMessageProgress>, CancellationToken, Task<BulkMessageAnalyzeResult>> analyzer,
        Func<BulkMessageRequest, IProgress<BulkMessageProgress>, CancellationToken, Task<BulkMessageSendResult>> sender,
        Func<CancellationToken, Task> clearCache,
        CancellationToken externalToken)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _analyzer = analyzer;
        _sender = sender;
        _clearCache = clearCache;
        _externalToken = externalToken;

        SortOrderComboBox.ItemsSource = new[]
        {
            new SortOption("Population highest", BulkMessageSortOrder.PopulationDescending),
            new SortOption("Population lowest", BulkMessageSortOrder.PopulationAscending),
        };
        SortOrderComboBox.SelectedIndex = 0;
        Loaded += OnLoaded;
        RefreshState();
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        CancelOperationCts();
        DisposeOperationCts();
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(AnalyzeAsync, LogUiGuardError);

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(AnalyzeAsync, LogUiGuardError);

    private async Task AnalyzeAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        RefreshState();
        LoadingOverlay.Show("Analyzing players", "Downloading map.sql...");
        var progress = new Progress<BulkMessageProgress>(ApplyProgress);
        var cts = CreateOperationCts();
        try
        {
            _lastAnalyzeResult = await _analyzer(BuildAnalyzeRequest(), progress, cts.Token);
            PlayersTextBlock.Text = BuildPlayersText(_lastAnalyzeResult);
            ValidationTextBlock.Text = _lastAnalyzeResult.EligiblePlayers > 0
                ? string.Empty
                : "No eligible players found with the current filters and sent cache.";
        }
        catch (OperationCanceledException)
        {
            ValidationTextBlock.Text = "Analysis canceled.";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = ex.Message;
        }
        finally
        {
            DisposeOperationCts();
            LoadingOverlay.Hide();
            _busy = false;
            RefreshState();
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(SendAsync, LogUiGuardError);

    private async Task SendAsync()
    {
        if (_busy)
        {
            return;
        }

        if (!TryBuildSendRequest(out var request, out var validation))
        {
            ValidationTextBlock.Text = validation;
            return;
        }

        _busy = true;
        RefreshState();
        LoadingOverlay.Show("Sending messages", "Preparing recipients...");
        var progress = new Progress<BulkMessageProgress>(ApplyProgress);
        var cts = CreateOperationCts();
        try
        {
            var result = await _sender(request, progress, cts.Token);
            PlayersTextBlock.Text = $"Players: {result.PlayersAnalyzed} | Sent now: {result.SentCount}/{result.TargetCount} | Cached before: {result.SentCachedCount}";
            ValidationTextBlock.Text = $"Sent {result.SentCount}/{result.TargetCount} messages.";
        }
        catch (OperationCanceledException)
        {
            ValidationTextBlock.Text = "Sending canceled.";
        }
        catch (Exception ex)
        {
            ValidationTextBlock.Text = ex.Message;
        }
        finally
        {
            DisposeOperationCts();
            LoadingOverlay.Hide();
            _busy = false;
            RefreshState();
        }
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(ClearCacheAsync, LogUiGuardError);

    private async Task ClearCacheAsync()
    {
        if (_busy)
        {
            return;
        }

        var cts = CreateOperationCts();
        try
        {
            await _clearCache(cts.Token);
            ValidationTextBlock.Text = "Players cache cleared.";
        }
        finally
        {
            DisposeOperationCts();
        }

        await AnalyzeAsync();
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        RefreshState();
    }

    private void LoadingOverlay_Cancelled(object sender, EventArgs e)
    {
        CancelOperationCts();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelOperationCts();
        Close();
    }

    private CancellationTokenSource CreateOperationCts()
    {
        _operationCts?.Dispose();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_externalToken);
        return _operationCts;
    }

    private void CancelOperationCts()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeOperationCts()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private BulkMessageAnalyzeRequest BuildAnalyzeRequest()
    {
        return new BulkMessageAnalyzeRequest(
            ParseSemicolonList(ExcludePlayersTextBox.Text),
            ParseSemicolonList(ExcludeAlliancesTextBox.Text),
            GetSortOrder());
    }

    private bool TryBuildSendRequest(out BulkMessageRequest request, out string validation)
    {
        request = new BulkMessageRequest(string.Empty, string.Empty, 0, [], [], GetSortOrder());
        validation = string.Empty;

        var subject = SubjectTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(subject))
        {
            validation = "Subject is required.";
            return false;
        }

        var message = MessageTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            validation = "Message is required.";
            return false;
        }

        if (!int.TryParse(MaxRecipientsTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxRecipients)
            || maxRecipients <= 0)
        {
            validation = "Max recipients must be a positive number.";
            return false;
        }

        request = new BulkMessageRequest(
            subject,
            message,
            maxRecipients,
            ParseSemicolonList(ExcludePlayersTextBox.Text),
            ParseSemicolonList(ExcludeAlliancesTextBox.Text),
            GetSortOrder());
        return true;
    }

    private BulkMessageSortOrder GetSortOrder()
    {
        return SortOrderComboBox.SelectedItem is SortOption option
            ? option.SortOrder
            : BulkMessageSortOrder.PopulationDescending;
    }

    private void ApplyProgress(BulkMessageProgress value)
    {
        LoadingOverlay.Title = value.Phase;
        LoadingOverlay.Text = value.TotalBatches > 0
            ? $"Sent {value.SentCount}/{value.TargetCount}\nBatch {value.BatchNumber}/{value.TotalBatches}" +
              (string.IsNullOrWhiteSpace(value.CurrentPlayers) ? string.Empty : $"\nCurrent: {value.CurrentPlayers}")
            : value.TargetCount > 0
                ? $"Players: {value.TargetCount}"
                : "Working...";
    }

    private void RefreshState()
    {
        if (SendButton is null)
        {
            return;
        }

        AnalyzeButton.IsEnabled = !_busy;
        ClearCacheButton.IsEnabled = !_busy;
        SendButton.IsEnabled = !_busy && TryBuildSendRequest(out _, out _);
    }

    private static string BuildPlayersText(BulkMessageAnalyzeResult result)
    {
        return $"Players: {result.PlayersAnalyzed} | Eligible: {result.EligiblePlayers} | Cached sent: {result.SentCachedCount}";
    }

    private static IReadOnlyList<string> ParseSemicolonList(string? value)
    {
        return (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LogUiGuardError(string message)
    {
        ValidationTextBlock.Text = message;
    }

    private sealed record SortOption(string Label, BulkMessageSortOrder SortOrder)
    {
        public override string ToString() => Label;
    }
}
