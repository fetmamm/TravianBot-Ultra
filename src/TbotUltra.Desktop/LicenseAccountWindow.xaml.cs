using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbotUltra.Desktop.Services.Licensing;

namespace TbotUltra.Desktop;

internal sealed record LicenseCopyListItem(TbotLicenseCopy Copy, bool IsCurrent);

public partial class LicenseAccountWindow : Window
{
    private const string AccountWebsiteUrl = "https://tbot-ultra.pages.dev/account.html";
    private readonly TbotLicenseClient _client;
    private readonly LicenseCopyIdentity _copy;

    public event Action? SessionChanged;
    public event Func<Task>? SigningOut;

    public LicenseAccountWindow(TbotLicenseClient client, LicenseCopyIdentity copy)
    {
        InitializeComponent();
        _client = client;
        _copy = copy;
        BuyPanel.Visibility = Visibility.Collapsed;
        if (client.IsSignedIn)
        {
            ShowSignedInState(null, "Checking the saved account session.");
        }
        else
        {
            ShowSignedOutState();
        }
        Loaded += async (_, _) => await RunAsync(RefreshAsync, "Loading Tbot account...");
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var window = new LicenseAuthWindow(_client, _copy, createAccount: false, AccountWebsiteUrl)
        {
            Owner = this,
        };
        if (window.ShowDialog() == true)
            await RunAsync(RefreshAsync, "Loading account...");
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var window = new LicenseAuthWindow(_client, _copy, createAccount: true, AccountWebsiteUrl)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (SigningOut is not null) await SigningOut();
            _client.SignOut();
            LicenseText.Text = "Sign in to view your license.";
            CopiesList.ItemsSource = null;
            BuyPanel.Visibility = Visibility.Collapsed;
            ShowSignedOutState();
            SessionChanged?.Invoke();
        }, "Signing out...");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshAsync, "Refreshing account...");

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        var window = new RedeemCodeWindow(_client) { Owner = this };
        if (window.ShowDialog() != true) return;
        await RunAsync(async () =>
        {
            await RefreshAsync();
        }, "Refreshing license...");
    }

    private async void RemoveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CopiesList.SelectedItem is not LicenseCopyListItem item) return;
        await RunAsync(async () =>
        {
            await _client.RemoveCopyAsync(item.Copy.CopyId);
            await RefreshAsync();
        }, "Removing program copy...");
    }

    private async void Buy_Click(object sender, RoutedEventArgs e)
    {
        var selected = new[] { Package1Button, Package2Button, Package6Button, Package12Button }
            .FirstOrDefault(button => button.IsChecked == true);
        if (selected is null || !int.TryParse(selected.Tag?.ToString(), out var months)) return;
        await RunAsync(async () =>
        {
            var url = await _client.CreateCheckoutAsync(months);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }, "Opening checkout...");
    }

    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AccountWebsiteUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, ex.Message, "Open account website",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshAsync()
    {
        if (!_client.IsSignedIn)
        {
            ShowSignedOutState();
            return;
        }

        var status = await _client.GetStatusAsync();
        string? email = null;
        try
        {
            email = await _client.GetAccountEmailAsync();
        }
        catch (Exception)
        {
            // License status is still useful during a temporary account-profile lookup failure.
        }
        ShowSignedInState(email);
        LicenseText.Text = status.Tier switch
        {
            "pending" when status.PendingPaidMonths > 0 =>
                $"Your {status.PendingPaidMonths} prepaid month(s) start with your first automation run.",
            "pending" when status.PromoActivationAvailable =>
                "Your three free months start with your first automation run.",
            "pending" when status.PromoRedeemed =>
                "The activation window for your three-month code has expired.",
            "pending" => "Your three-day trial starts with your first automation run.",
            "full" when status.LifetimeAccess => "Lifetime access.",
            "full" => $"Full access until {status.FullAccessUntil?.ToLocalTime():g}.",
            _ => $"Free time left today: {TimeSpan.FromSeconds(status.SecondsRemaining):h\\:mm}. " +
                $"Resets at {status.NextResetAt.ToLocalTime():g}.",
        };
        BuyPanel.Visibility = Visibility.Visible;
        OpenCheckoutButton.IsEnabled = status.CheckoutEnabled;
        OpenCheckoutButton.ToolTip = status.CheckoutEnabled
            ? null
            : "Checkout is currently unavailable.";
        CopiesList.ItemsSource = (await _client.GetCopiesAsync())
            .Select(copy => new LicenseCopyListItem(copy, copy.CopyId == _copy.CopyId))
            .OrderByDescending(item => item.IsCurrent)
            .ToList();
        SessionChanged?.Invoke();
    }

    private void ShowSignedInState(string? email, string? detail = null)
    {
        AccountStatusTitle.Text = "Signed in";
        AccountStatusDetail.Text = detail ?? (string.IsNullOrWhiteSpace(email)
            ? "Your saved Tbot account session is active."
            : email);
        AccountStatusTitle.Foreground = (Brush)FindResource("SuccessTextBrush");
        SessionStatusBorder.Background = (Brush)FindResource("SurfaceAltBrush");
        SessionStatusBorder.BorderBrush = (Brush)FindResource("BorderSubtleBrush");
        SessionStatusDot.Fill = (Brush)FindResource("SuccessBrush");
        SessionStatusBorder.Visibility = Visibility.Visible;
        SignedOutActionsPanel.Visibility = Visibility.Collapsed;
        ProgramCopiesPanel.Visibility = Visibility.Visible;
        SignOutButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
    }

    private void ShowSignedOutState()
    {
        AccountStatusTitle.Text = "Not signed in";
        AccountStatusDetail.Text = "Sign in or create an account to use your license.";
        AccountStatusTitle.Foreground = (Brush)FindResource("TextPrimaryBrush");
        SessionStatusBorder.Background = (Brush)FindResource("SurfaceAltBrush");
        SessionStatusBorder.BorderBrush = (Brush)FindResource("BorderSubtleBrush");
        SessionStatusDot.Fill = (Brush)FindResource("TextSubtleBrush");
        SessionStatusBorder.Visibility = Visibility.Visible;
        SignedOutActionsPanel.Visibility = Visibility.Visible;
        ProgramCopiesPanel.Visibility = Visibility.Collapsed;
        SignOutButton.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;
    }

    private async Task RunAsync(Func<Task> action, string loadingMessage)
    {
        LoadingText.Text = loadingMessage;
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (_client.IsSignedIn)
            {
                ShowSignedInState(null, "Your saved session is active, but account details could not be loaded.");
            }
            else
            {
                ShowSignedOutState();
            }
            AppDialog.Show(this, ex.Message, "Tbot account",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }
}
