using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TbotUltra.Desktop.Services.Licensing;

namespace TbotUltra.Desktop;

public partial class LicenseAuthWindow : Window
{
    private readonly TbotLicenseClient _client;
    private readonly LicenseCopyIdentity _copy;
    private readonly bool _createAccount;
    private readonly string _accountWebsiteUrl;

    public LicenseAuthWindow(
        TbotLicenseClient client,
        LicenseCopyIdentity copy,
        bool createAccount,
        string accountWebsiteUrl)
    {
        InitializeComponent();
        _client = client;
        _copy = copy;
        _createAccount = createAccount;
        _accountWebsiteUrl = accountWebsiteUrl;
        Title = createAccount ? "Create Tbot account" : "Sign in to Tbot";
        HeadingText.Text = createAccount ? "Create account" : "Sign in";
        DescriptionText.Text = createAccount
            ? "Create the account that your license will be linked to."
            : "Sign in to access your license on this program copy.";
        SubmitButton.Content = createAccount ? "Create account" : "Sign in";
        EmailBox.Text = client.RememberedEmail ?? string.Empty;
        ConfirmPasswordPanel.Visibility = createAccount ? Visibility.Visible : Visibility.Collapsed;
        Height = createAccount ? 410 : 340;
        Loaded += (_, _) => EmailBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = ReadPassword(PasswordBox, VisiblePasswordBox, PasswordRevealButton);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Enter your email and password.");
            return;
        }
        if (_createAccount && password.Length < 6)
        {
            ShowError("Use a password with at least 6 characters.");
            return;
        }
        if (_createAccount)
        {
            var confirmation = ReadPassword(ConfirmPasswordBox, VisibleConfirmPasswordBox,
                ConfirmPasswordRevealButton);
            if (string.IsNullOrWhiteSpace(confirmation))
            {
                ShowError("Enter the password again.");
                return;
            }
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                ShowError("The passwords do not match.");
                return;
            }
        }

        ErrorText.Text = string.Empty;
        LoadingText.Text = _createAccount ? "Creating account..." : "Signing in...";
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            if (_createAccount)
            {
                await _client.SignUpAsync(email, password);
                PasswordBox.Clear();
                VisiblePasswordBox.Clear();
                ConfirmPasswordBox.Clear();
                VisibleConfirmPasswordBox.Clear();
                AppDialog.Show(this, "Check your email to verify the account, then sign in.",
                    "Account created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                await _client.SignInAsync(email, password);
                PasswordBox.Clear();
                VisiblePasswordBox.Clear();
                var copyLabel = $"{Environment.MachineName} ({_copy.CopyId.ToString("N")[..8]})";
                await _client.RegisterCopyAsync(_copy.CopyId, _copy.MachineFingerprintHash,
                    copyLabel[..Math.Min(copyLabel.Length, 80)]);
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void PasswordReveal_Changed(object sender, RoutedEventArgs e)
        => SetPasswordVisibility(PasswordBox, VisiblePasswordBox, PasswordRevealButton);

    private void ConfirmPasswordReveal_Changed(object sender, RoutedEventArgs e)
        => SetPasswordVisibility(ConfirmPasswordBox, VisibleConfirmPasswordBox,
            ConfirmPasswordRevealButton);

    private static string ReadPassword(PasswordBox hiddenBox, TextBox visibleBox,
        ToggleButton revealButton)
        => revealButton.IsChecked == true ? visibleBox.Text : hiddenBox.Password;

    private static void SetPasswordVisibility(PasswordBox hiddenBox, TextBox visibleBox,
        ToggleButton revealButton)
    {
        if (revealButton.IsChecked == true)
        {
            visibleBox.Text = hiddenBox.Password;
            hiddenBox.Visibility = Visibility.Collapsed;
            visibleBox.Visibility = Visibility.Visible;
            visibleBox.Focus();
            visibleBox.CaretIndex = visibleBox.Text.Length;
            revealButton.ToolTip = "Hide password";
            return;
        }

        hiddenBox.Password = visibleBox.Text;
        visibleBox.Visibility = Visibility.Collapsed;
        hiddenBox.Visibility = Visibility.Visible;
        hiddenBox.Focus();
        revealButton.ToolTip = "Show password";
    }

    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_accountWebsiteUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        Debug.WriteLine($"Tbot account authentication message: {message}");
    }
}
