using System.Diagnostics;
using System.Windows;
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
        Loaded += (_, _) => EmailBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;
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

        ErrorText.Text = string.Empty;
        LoadingText.Text = _createAccount ? "Creating account..." : "Signing in...";
        LoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            if (_createAccount)
            {
                await _client.SignUpAsync(email, password);
                PasswordBox.Clear();
                AppDialog.Show(this, "Check your email to verify the account, then sign in.",
                    "Account created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                await _client.SignInAsync(email, password);
                PasswordBox.Clear();
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
