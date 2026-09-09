using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class ProductionBonusDeletionSourceTests
{
    [Fact]
    public void DeletionResult_DisablesOnlyActiveAccountsProductionBonus()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Desktop", "MainWindow.ProductionBonus.cs"));

        Assert.Contains("ParseAccountDeletionPendingToken(message)", source, StringComparison.Ordinal);
        Assert.Contains("config[BotOptionPayloadKeys.ProductionBonusVideoEnabled] = false;", source, StringComparison.Ordinal);
        Assert.Contains("_botConfigStore.SaveForAccount(account, config);", source, StringComparison.Ordinal);
        Assert.Contains("ProductionBonusVideoCheckBox.IsChecked = false;", source, StringComparison.Ordinal);
        Assert.Contains("RemovePendingProductionBonus();", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
