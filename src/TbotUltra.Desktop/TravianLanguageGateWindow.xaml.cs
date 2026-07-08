using System.ComponentModel;
using System.Windows;

namespace TbotUltra.Desktop;

public partial class TravianLanguageGateWindow : Window
{
    private const string ExpectedLanguage = "en-US";
    private readonly Func<Task<string?>> _setAutomatically;
    private readonly Func<Task<string?>> _readCurrentLanguage;
    private bool _verified;

    public TravianLanguageGateWindow(
        string? currentLanguage,
        Func<Task<string?>> setAutomatically,
        Func<Task<string?>> readCurrentLanguage)
    {
        InitializeComponent();
        ThemeChrome.EnableEarlyDarkTitleBar(this);
        _setAutomatically = setAutomatically;
        _readCurrentLanguage = readCurrentLanguage;
        SetCurrentLanguage(currentLanguage);
        StatusTextBlock.Text = "Set the Travian language to English, then verify it here.";
    }

    private async void AutoButton_Click(object sender, RoutedEventArgs e)
    {
        await RunLanguageActionAsync("Setting language to English...", _setAutomatically);
    }

    private async void ManualButton_Click(object sender, RoutedEventArgs e)
    {
        await RunLanguageActionAsync("Checking current language...", _readCurrentLanguage);
    }

    private async Task RunLanguageActionAsync(string busyText, Func<Task<string?>> action)
    {
        SetBusy(true, busyText);
        try
        {
            var language = await action();
            SetCurrentLanguage(language);
            if (IsExpectedLanguage(language))
            {
                _verified = true;
                DialogResult = true;
                Close();
                return;
            }

            StatusTextBlock.Text = "Language is still not English. Set Travian language to English and try again.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not verify language: {ex.Message}";
        }
        finally
        {
            if (!_verified)
            {
                SetBusy(false, StatusTextBlock.Text);
            }
        }
    }

    private void SetBusy(bool busy, string status)
    {
        AutoButton.IsEnabled = !busy;
        ManualButton.IsEnabled = !busy;
        StatusTextBlock.Text = status;
    }

    private void SetCurrentLanguage(string? language)
    {
        var display = string.IsNullOrWhiteSpace(language) ? "unknown" : language.Trim();
        CurrentLanguageTextBlock.Text = $"Current language: {display}. Required language: {ExpectedLanguage}.";
    }

    private static bool IsExpectedLanguage(string? language)
        => string.Equals(language?.Trim(), ExpectedLanguage, StringComparison.OrdinalIgnoreCase);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_verified)
        {
            e.Cancel = true;
            StatusTextBlock.Text = "This popup stays open until Travian language is verified as English.";
        }
    }
}
