using System.Windows;
using TbotUltra.Desktop.Services.Licensing;

namespace TbotUltra.Desktop;

public partial class RedeemCodeWindow : Window
{
    private readonly TbotLicenseClient _client;

    public RedeemCodeWindow(TbotLicenseClient client)
    {
        InitializeComponent();
        _client = client;
        Loaded += (_, _) => CodeBox.Focus();
    }

    private async void Redeem_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            ErrorText.Text = "Paste your code first.";
            CodeBox.Focus();
            return;
        }

        RedeemContent.IsEnabled = false;
        RedeemLoadingOverlay.Visibility = Visibility.Visible;
        ErrorText.Text = string.Empty;
        try
        {
            await _client.RedeemCodeAsync(code);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = FriendlyRedeemError(ex.Message);
        }
        finally
        {
            RedeemLoadingOverlay.Visibility = Visibility.Collapsed;
            RedeemContent.IsEnabled = true;
        }
    }

    internal static string FriendlyRedeemError(string message)
    {
        if (message.Contains("Invalid or already redeemed", StringComparison.OrdinalIgnoreCase))
            return "That code is invalid or has already been used.";
        if (message.Contains("already redeemed on this account", StringComparison.OrdinalIgnoreCase))
            return "This account has already redeemed an existing-user code.";
        if (message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            return "The redemption period for existing-user codes has ended.";
        return message;
    }
}
