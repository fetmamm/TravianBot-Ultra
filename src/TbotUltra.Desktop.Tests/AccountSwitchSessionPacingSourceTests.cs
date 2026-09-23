using TbotUltra.Worker;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AccountSwitchSessionPacingSourceTests
{
    [Fact]
    public void RefreshAfterActiveAccountChanged_ForceClearsThePreviousAccountsVillageSelection()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Session.cs"));
        var methodStart = source.IndexOf(
            "private void RefreshAfterActiveAccountChanged",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    // ResetVillageSelectionUi()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];

        Assert.Contains("ForceClearVillageSelectionUi();", methodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetVillageSelectionUi();", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetForAccountSwitch_ClearsThePreviousAccountsSleepState()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.Session.cs"));
        var methodStart = source.IndexOf(
            "private async Task ResetForAccountSwitchAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    // bot.json is shared across accounts",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];

        Assert.Contains("ResetSessionPacing();", methodBody, StringComparison.Ordinal);

        var pacingSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.SessionPacing.cs"));
        var resetStart = pacingSource.IndexOf(
            "private void ResetSessionPacing()",
            StringComparison.Ordinal);
        var resetEnd = pacingSource.IndexOf(
            "    // Freeze the pacing run->sleep countdown",
            resetStart,
            StringComparison.Ordinal);

        Assert.True(resetStart >= 0 && resetEnd > resetStart);
        var resetBody = pacingSource[resetStart..resetEnd];
        Assert.Contains("_sleepSnapshot = SleepSnapshot.Idle;", resetBody, StringComparison.Ordinal);
        Assert.Contains("_sessionPacer.Reset();", resetBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveSessionExtensionDialog_OffersBlueSleepNowAction()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.SessionPacing.cs"));
        var methodStart = source.IndexOf(
            "private void SessionPacingExtendButton_Click",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private void UpdateSessionPacingUi()",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        var cancelIndex = methodBody.IndexOf("(\"Cancel\", MessageBoxResult.Cancel)", StringComparison.Ordinal);
        var sleepIndex = methodBody.IndexOf("(\"Sleep now\", MessageBoxResult.No)", StringComparison.Ordinal);
        var extendIndex = methodBody.IndexOf("(\"Extend session\", MessageBoxResult.Yes)", StringComparison.Ordinal);

        Assert.True(cancelIndex >= 0 && sleepIndex > cancelIndex && extendIndex > sleepIndex);
        Assert.Contains("accentResult: MessageBoxResult.No", methodBody, StringComparison.Ordinal);
        Assert.Contains("if (result == MessageBoxResult.No)", methodBody, StringComparison.Ordinal);
        Assert.Contains("RequestManualSessionSleep();", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SmartSleepHeaderAction_ConfirmsSharedManualSleepAndPreservesCurrentWork()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.xaml"));
        Assert.Contains("x:Name=\"SmartSleepNowButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"&#xE708;\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource InfoBgBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SmartSleepNowButton_Click\"", xaml, StringComparison.Ordinal);

        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.SessionPacing.cs"));
        var methodStart = source.IndexOf(
            "private void SmartSleepNowButton_Click",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private void SessionPacingExtendButton_Click",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        Assert.Contains("_sessionSleepMinMinutes", methodBody, StringComparison.Ordinal);
        Assert.Contains("_sessionSleepMaxMinutes", methodBody, StringComparison.Ordinal);
        Assert.Contains("[(\"Cancel\", MessageBoxResult.Cancel), (\"Sleep now\", MessageBoxResult.Yes)]", methodBody, StringComparison.Ordinal);
        Assert.Contains("MessageBoxResult.Cancel,", methodBody, StringComparison.Ordinal);
        Assert.Contains("RequestManualSessionSleep();", methodBody, StringComparison.Ordinal);
        Assert.Contains("ShowSleepExtensionDialog(warnAboutDelayedTasks: true);", methodBody, StringComparison.Ordinal);

        Assert.Contains("SmartSleepNowButton.Visibility = _smartSleepSettings.Enabled", source, StringComparison.Ordinal);
        Assert.Contains("SmartSleepNowButton.ToolTip = IsSessionSleeping ? \"Extend sleep\" : \"Sleep now\";", source, StringComparison.Ordinal);
        Assert.Contains("&& !_smartSleepSettings.Enabled", source, StringComparison.Ordinal);
        Assert.Contains("RequestAutomationStop(AutomationStopMode.AfterCurrentAction);", source, StringComparison.Ordinal);
        Assert.Contains("HandleSessionPacingSleepStartingAsync(manual)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SmartSleepExtensionDialog_ShowsIntervalsNewWakeAndDelayWarning()
    {
        var projectRoot = ProjectRootLocator.FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "TbotUltra.Desktop",
            "MainWindow.SessionPacing.cs"));
        var methodStart = source.IndexOf(
            "private void ShowSleepExtensionDialog",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "    private void SessionPacingExtendButton_Click",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var methodBody = source[methodStart..methodEnd];
        Assert.Contains("new[] { \"5 minutes\", \"10 minutes\", \"20 minutes\", \"30 minutes\", \"60 minutes\" }", methodBody, StringComparison.Ordinal);
        Assert.Contains("SelectedItem = \"20 minutes\"", methodBody, StringComparison.Ordinal);
        Assert.Contains("New wake:", methodBody, StringComparison.Ordinal);
        Assert.Contains("Planned tasks may be delayed.", methodBody, StringComparison.Ordinal);
        Assert.Contains("_sessionPacer.ExtendSleep", methodBody, StringComparison.Ordinal);
    }
}
