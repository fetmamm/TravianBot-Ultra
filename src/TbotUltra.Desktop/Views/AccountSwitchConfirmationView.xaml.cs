using System.Windows.Controls;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Views;

public partial class AccountSwitchConfirmationView : UserControl
{
    public AccountSwitchConfirmationView(AccountEntry? currentAccount, AccountEntry targetAccount)
    {
        InitializeComponent();
        DataContext = new AccountSwitchConfirmationModel(
            BuildCard(currentAccount, "Current account"),
            BuildCard(targetAccount, "Selected account"));
    }

    private static AccountSwitchCardModel BuildCard(AccountEntry? account, string fallbackTitle)
    {
        var title = account is null
            ? fallbackTitle
            : string.IsNullOrWhiteSpace(account.Username) ? account.Name : account.Username.Trim();
        var accountName = account is null || string.IsNullOrWhiteSpace(account.Name)
            ? "Saved account"
            : account.Name.Trim();
        var initialSource = title.Trim();
        var initial = initialSource.Length > 0
            ? initialSource[..1].ToUpperInvariant()
            : "?";

        return new AccountSwitchCardModel(
            title,
            accountName,
            account?.ServerDisplayName ?? "-",
            initial);
    }

}

public sealed record AccountSwitchConfirmationModel(
    AccountSwitchCardModel Current,
    AccountSwitchCardModel Target);

public sealed record AccountSwitchCardModel(
    string Title,
    string AccountName,
    string Server,
    string Initial);
