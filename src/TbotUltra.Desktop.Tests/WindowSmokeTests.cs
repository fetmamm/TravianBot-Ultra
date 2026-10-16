using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Services.Licensing;
using TbotUltra.Desktop.ViewModels;
using TbotUltra.Desktop.Views;
using Xunit;

namespace TbotUltra.Desktop.Tests;

/// <summary>
/// Constructs the standalone windows and asserts the parts that are decided at load time. These cover
/// the wiring unit tests cannot reach: XAML parsing, resource lookups, and the column visibility rules
/// applied in the constructor.
/// </summary>
[Collection(WpfSmokeCollection.Name)]
public sealed class WindowSmokeTests
{
    private readonly WpfSmokeFixture _wpf;

    public WindowSmokeTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void LicenseAccountWindow_ShowsClearSignedOutStateAndLoadingCard()
    {
        _wpf.Run(() =>
        {
            using var client = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"));
            var window = new LicenseAccountWindow(client,
                new LicenseCopyIdentity(Guid.NewGuid(), new string('a', 64)));
            try
            {
                Assert.Equal("Not signed in", Assert.IsType<TextBlock>(
                    window.FindName("AccountStatusTitle")).Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(
                    window.FindName("SessionStatusBorder")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(
                    window.FindName("SignedOutActionsPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(
                    window.FindName("ProgramCopiesPanel")).Visibility);
                var programCopiesPanel = Assert.IsType<Border>(window.FindName("ProgramCopiesPanel"));
                var buyPanel = Assert.IsType<Border>(window.FindName("BuyPanel"));
                var accountCards = Assert.IsType<StackPanel>(programCopiesPanel.Parent);
                Assert.True(accountCards.Children.IndexOf(programCopiesPanel) <
                            accountCards.Children.IndexOf(buyPanel));
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(
                    window.FindName("LoadingOverlay")).Visibility);
                Assert.Equal("Open website", Assert.IsType<Button>(
                    window.FindName("OpenWebsiteButton")).Content);
                var selectedPackage = Assert.IsType<RadioButton>(
                    window.FindName("Package2Button"));
                Assert.True(selectedPackage.IsChecked);
                selectedPackage.ApplyTemplate();
                var selectedCard = Assert.IsType<Border>(
                    selectedPackage.Template.FindName("Card", selectedPackage));
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(window.FindResource("SuccessBorderBrush")).Color,
                    Assert.IsType<SolidColorBrush>(selectedCard.BorderBrush).Color);
                Assert.False(Assert.IsType<RadioButton>(
                    window.FindName("Package6Button")).IsChecked);
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(window.FindResource("ControlBackgroundBrush")).Color,
                    Assert.IsType<SolidColorBrush>(Assert.IsType<ListBox>(
                        window.FindName("CopiesList")).Background).Color);
                var removeCopyBackground = Assert.IsType<SolidColorBrush>(Assert.IsType<Button>(
                    window.FindName("RemoveCopyButton")).Background).Color;
                Assert.Equal(removeCopyBackground, Assert.IsType<SolidColorBrush>(Assert.IsType<Button>(
                    window.FindName("OpenCheckoutButton")).Background).Color);
                Assert.Equal(removeCopyBackground, Assert.IsType<SolidColorBrush>(Assert.IsType<Button>(
                    window.FindName("RedeemCodeButton")).Background).Color);
                Assert.Null(window.FindName("MessagePanel"));
                var signInButton = Assert.IsType<Button>(window.FindName("SignInButton"));
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(window.FindResource("SuccessBgBrush")).Color,
                    Assert.IsType<SolidColorBrush>(signInButton.Background).Color);
                Assert.Equal("Create account", Assert.IsType<Button>(
                    window.FindName("CreateButton")).Content);
                Assert.Null(window.FindName("AuthPanel"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LicenseAuthWindow_ValidatesCredentialsBeforeRequest()
    {
        _wpf.Run(() =>
        {
            using var client = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"));
            var window = new LicenseAuthWindow(client,
                new LicenseCopyIdentity(Guid.NewGuid(), new string('a', 64)),
                createAccount: false,
                "https://example.com/account");
            try
            {
                Assert.Equal("Sign in", Assert.IsType<Button>(
                    window.FindName("SubmitButton")).Content);
                Assert.Equal("Open website", Assert.IsType<Button>(
                    window.FindName("OpenWebsiteButton")).Content);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(
                    window.FindName("ConfirmPasswordPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(
                    window.FindName("LoadingOverlay")).Visibility);

                Assert.IsType<Button>(window.FindName("SubmitButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var error = Assert.IsType<TextBlock>(window.FindName("ErrorText"));
                Assert.Equal("Enter your email and password.", error.Text);
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(window.FindResource("DangerTextBrush")).Color,
                    Assert.IsType<SolidColorBrush>(error.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LicenseAuthWindow_ConfirmsNewPasswordAndCanRevealIt()
    {
        _wpf.Run(() =>
        {
            using var client = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"));
            var window = new LicenseAuthWindow(client,
                new LicenseCopyIdentity(Guid.NewGuid(), new string('a', 64)),
                createAccount: true,
                "https://example.com/account");
            try
            {
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(
                    window.FindName("ConfirmPasswordPanel")).Visibility);
                Assert.Equal("Open website", Assert.IsType<Button>(
                    window.FindName("OpenWebsiteButton")).Content);

                Assert.IsType<TextBox>(window.FindName("EmailBox")).Text = "person@example.com";
                var passwordBox = Assert.IsType<PasswordBox>(window.FindName("PasswordBox"));
                passwordBox.Password = "password";
                var revealButton = Assert.IsType<ToggleButton>(
                    window.FindName("PasswordRevealButton"));
                revealButton.IsChecked = true;
                Assert.Equal(Visibility.Visible, Assert.IsType<TextBox>(
                    window.FindName("VisiblePasswordBox")).Visibility);
                Assert.Equal("password", Assert.IsType<TextBox>(
                    window.FindName("VisiblePasswordBox")).Text);

                Assert.IsType<PasswordBox>(window.FindName("ConfirmPasswordBox")).Password = "different";
                Assert.IsType<Button>(window.FindName("SubmitButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("The passwords do not match.", Assert.IsType<TextBlock>(
                    window.FindName("ErrorText")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RedeemCodeWindow_ExplainsEmptyAndInvalidCodes()
    {
        _wpf.Run(() =>
        {
            using var client = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"));
            var window = new RedeemCodeWindow(client);
            try
            {
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(
                    window.FindName("RedeemLoadingOverlay")).Visibility);
                Assert.Equal("That code is invalid or has already been used.",
                    RedeemCodeWindow.FriendlyRedeemError(
                        "Invalid or already redeemed promotion code"));

                var redeemButton = Assert.IsType<Button>(window.FindName("RedeemButton"));
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(window.FindResource("SuccessBgBrush")).Color,
                    Assert.IsType<SolidColorBrush>(redeemButton.Background).Color);
                redeemButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("Paste your code first.", Assert.IsType<TextBlock>(
                    window.FindName("ErrorText")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UpgradeTroopsTab_ShowsPerVillageListOffscreen()
    {
        _wpf.Run(() =>
        {
            var vm = new TroopTrainingViewModel();
            vm.SmithyVillages.Add(new SmithyUpgradeVillageRow(
                new VillageSettingsStore.VillageKeyInfo("xy:1|2", "Home", 1, 2, false), 2, true));
            var panel = new TroopsHubPanel { DataContext = vm };
            var selectionNotifications = 0;
            panel.UpgradeTroopsSelected += () => selectionNotifications++;
            var window = new Window { Content = panel };
            try
            {
                WpfSmokeFixture.PrepareOffscreen(window);
                window.Show();
                Assert.IsType<TabItem>(panel.FindName("UpgradeTroopsTabItem")).IsSelected = true;
                window.UpdateLayout();

                Assert.True(panel.IsUpgradeTroopsSelected);
                Assert.Equal(1, selectionNotifications);
                var list = Assert.IsType<ItemsControl>(panel.UpgradeTroopsPanel.FindName("SmithyVillagesList"));
                Assert.Single(list.Items);
                Assert.Equal("Home", Assert.IsType<SmithyUpgradeVillageRow>(list.Items[0]).VillageName);
                var presenter = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var toggle = Assert.IsType<CheckBox>(presenter.ContentTemplate.FindName("SmithyVillageToggle", presenter));
                var settings = Assert.IsType<Button>(presenter.ContentTemplate.FindName("SmithyVillageSettingsButton", presenter));
                Assert.Same(vm.ToggleSmithyVillageCommand, toggle.Command);
                Assert.Same(vm.OpenSmithyVillageSettingsCommand, settings.Command);
                Assert.Same(list.Items[0], toggle.CommandParameter);
                Assert.Same(list.Items[0], settings.CommandParameter);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UpgradeTroopsTab_ShowsGreenToggleBeforeVillageAndWarnsWhenNoTroopsSelected()
    {
        _wpf.Run(() =>
        {
            var vm = new TroopTrainingViewModel();
            vm.SmithyVillages.Add(new SmithyUpgradeVillageRow(
                new VillageSettingsStore.VillageKeyInfo("xy:1|2", "Home", 1, 2, false), 0, false));
            var panel = new TroopsHubPanel { DataContext = vm };
            var window = new Window { Content = panel };
            try
            {
                WpfSmokeFixture.PrepareOffscreen(window);
                window.Show();
                Assert.IsType<TabItem>(panel.FindName("UpgradeTroopsTabItem")).IsSelected = true;
                window.UpdateLayout();

                var troopsPanel = panel.UpgradeTroopsPanel;
                var description = Assert.IsType<TextBlock>(troopsPanel.FindName("SmithyUpgradeDescription"));
                Assert.Contains("runs Smithy upgrades independently of troop building", description.Text);
                Assert.Equal(0, Grid.GetColumn(Assert.IsType<TextBlock>(troopsPanel.FindName("SmithyEnabledHeader"))));
                Assert.Equal(1, Grid.GetColumn(Assert.IsType<TextBlock>(troopsPanel.FindName("SmithyVillageHeader"))));

                var list = Assert.IsType<ItemsControl>(troopsPanel.FindName("SmithyVillagesList"));
                var presenter = Assert.IsType<ContentPresenter>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var toggle = Assert.IsType<CheckBox>(presenter.ContentTemplate.FindName("SmithyVillageToggle", presenter));
                var summary = Assert.IsType<TextBlock>(presenter.ContentTemplate.FindName("SmithySelectionSummary", presenter));
                Assert.Equal(0, Grid.GetColumn(toggle));
                Assert.Same(troopsPanel.FindResource("ToggleSwitchStyle"), toggle.Style);
                Assert.Equal("No troops selected", summary.Text);
                Assert.Equal(
                    Assert.IsType<SolidColorBrush>(troopsPanel.FindResource("WarningTextBrush")).Color,
                    Assert.IsType<SolidColorBrush>(summary.Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StartupProgressWindow_HasMatchingControlsAndCanCancelOffscreen()
    {
        _wpf.Run(() =>
        {
            var window = new StartupProgressWindow();
            var canceled = false;
            window.CancelRequested += (_, _) => canceled = true;
            try
            {
                WpfSmokeFixture.PrepareOffscreen(window);
                window.Show();
                window.SetProgress(45, "Loading settings...");
                Assert.Equal(45, Assert.IsType<ProgressBar>(window.FindName("LaunchProgress")).Value);
                Assert.Equal("Loading settings...", Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
                var minimize = Assert.IsType<Button>(window.FindName("MinimizeButton"));
                var close = Assert.IsType<Button>(window.FindName("CloseButton"));
                Assert.Equal(minimize.Width, close.Width);
                Assert.Equal(minimize.Height, close.Height);
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(canceled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BuildingSlotActionsWindow_ShowsWatchtowerTargetActionForEligibleWall()
    {
        _wpf.Run(() =>
        {
            var wall = new BuildingSlotRow
            {
                SlotId = 40,
                Name = "Stone Wall",
                Level = 20,
                Gid = 31,
                IsWallSlot = true,
                WatchtowerLevel = 6,
                WatchtowerProjectedLevel = 7,
                CanQueueWatchtowers = true,
            };
            var window = new BuildingSlotActionsWindow(
                wall,
                canDemolish: false,
                demolishRequirementText: string.Empty);
            try
            {
                var button = Assert.IsType<Button>(window.FindName("WatchtowerButton"));
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.Equal("Upgrade watchtowers...", button.Content);

                Assert.Equal(7, wall.WatchtowerUpgradeBaseLevel);
                WpfSmokeFixture.PrepareOffscreen(window);
                window.Loaded += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(window.ShowDialog());
                Assert.Equal(BuildingSlotAction.UpgradeWatchtowers, window.SelectedAction);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BuildingUpgradeTargetWindow_ShowsWatchtowerLevelsAndEstimate()
    {
        _wpf.Run(() =>
        {
            var wall = new BuildingSlotRow
            {
                SlotId = 40,
                Name = "Stone Wall",
                Level = 20,
                IsWallSlot = true,
                WatchtowerLevel = 18,
                WatchtowerProjectedLevel = 19,
                CanQueueWatchtowers = true,
            };
            var baseLevel = Assert.IsType<int>(wall.WatchtowerUpgradeBaseLevel);
            var targetRow = new BuildingSlotRow { SlotId = 40, Name = "Watchtowers", Level = baseLevel };
            var window = new BuildingUpgradeTargetWindow(
                targetRow,
                20,
                level => new BuildingNextLevelEstimate(level, 60, "1m", "100", "200", "300", "400"));
            try
            {
                var levels = Assert.IsType<ComboBox>(window.FindName("TargetLevelComboBox"));
                Assert.Equal(20, Assert.Single(levels.Items));
                Assert.Equal(20, levels.SelectedItem);
                Assert.Equal("Upgrade Watchtowers", Assert.IsType<TextBlock>(window.FindName("TitleTextBlock")).Text);
                Assert.Equal("1m", Assert.IsType<TextBlock>(window.FindName("TimeTextBlock")).Text);
                Assert.Equal("100", Assert.IsType<TextBlock>(window.FindName("WoodTextBlock")).Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("EstimateBorder")).Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AccountsWindow_EnablesSaveWhenExistingAccountDisplayNameChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tbot-accounts-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        try
        {
            var store = new EnvAccountStore(Path.Combine(root, ".env"));
            var server = new ServerOption { Name = "International - x5", BaseUrl = "https://example.com" };
            store.SaveAccount(
                new AccountEntry
                {
                    Name = "account",
                    Username = "player@example.com",
                    Password = "secret",
                    ServerName = server.Name,
                    ServerUrl = server.BaseUrl,
                },
                setActive: true);
            var configStore = new BotConfigStore(
                Path.Combine(root, "config", "bot.json"),
                root,
                store.ActiveAccountName);
            var deletionService = new AccountDeletionService(
                root,
                store,
                configStore,
                new TbotUltra.Worker.Services.JsonQueueStore(Path.Combine(root, "queue.json")));

            _wpf.Run(() =>
            {
                var window = new AccountsWindow(
                    store,
                    deletionService,
                    new ServerCatalogStore(Path.Combine(root, "servers.json")),
                    server.Name,
                    server.BaseUrl,
                    [server],
                    [server]);
                var displayName = Assert.IsType<TextBox>(window.FindName("DisplayNameTextBox"));
                var save = Assert.IsType<Button>(window.FindName("SaveButton"));
                var originalDisplayName = displayName.Text;
                try
                {
                    Assert.False(save.IsEnabled);

                    displayName.Text = "ROG X5";

                    Assert.True(save.IsEnabled);
                }
                finally
                {
                    displayName.Text = originalDisplayName;
                    window.Close();
                }
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TroopSettingsWindows_LoadWithSyncControlsAndAllTargetsSelected()
    {
        _wpf.Run(() =>
        {
            var payload = TroopTrainingQuickSettings.FromOptions(new BotOptions());
            var rows = new[]
            {
                new TroopTrainingQuickVillageRow("v1", "Village 1", true, payload, "Teutons"),
                new TroopTrainingQuickVillageRow("v2", "Village 2", false, payload, "Teutons"),
            };
            var optionsWindow = new TroopTrainingOptionsWindow(rows);
            var syncWindow = new TroopTrainingSettingsSyncWindow(rows);
            try
            {
                optionsWindow.Measure(new Size(980, 600));
                optionsWindow.Arrange(new Rect(0, 0, 980, 600));
                syncWindow.Measure(new Size(520, 520));
                syncWindow.Arrange(new Rect(0, 0, 520, 520));

                var syncButton = Assert.IsType<Button>(optionsWindow.FindName("SyncSettingsButton"));
                var source = Assert.IsType<ComboBox>(syncWindow.FindName("SourceVillageComboBox"));
                Assert.Equal("Sync settings", syncButton.Content);
                Assert.Equal(2, source.Items.Count);
                Assert.All(syncWindow.Targets, item => Assert.True(item.IsSelected));
                Assert.Single(syncWindow.Targets, item => item.IsSource);
            }
            finally
            {
                syncWindow.Close();
                optionsWindow.Close();
            }
        });
    }

    [Fact]
    public void ChromiumSetupWindow_LoadsAsAConsentPromptWithNoInFlightState()
    {
        _wpf.Run(() =>
        {
            var window = new ChromiumSetupWindow();
            try
            {
                window.Measure(new Size(470, 205));
                window.Arrange(new Rect(0, 0, 470, 205));

                var download = Assert.IsType<Button>(window.FindName("DownloadButton"));
                var cancel = Assert.IsType<Button>(window.FindName("NotNowButton"));
                Assert.True(cancel.IsCancel, "Cancel must stay IsCancel so Esc dismisses the prompt.");
                Assert.True(download.IsDefault, "Download must stay the default so Enter accepts.");

                // The download runs behind the shared busy overlay, so this window must own no progress
                // state — an earlier version blocked its own close while an install was in flight.
                Assert.Null(window.FindName("DownloadProgressBar"));
                Assert.Null(window.FindName("StatusTextBlock"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VersionWindow_LoadsWithDeterminateUpdateOverlay()
    {
        _wpf.Run(() =>
        {
            var window = new VersionWindow("1.0.0", status: null);
            try
            {
                window.Measure(new Size(440, 340));
                window.Arrange(new Rect(0, 0, 440, 340));

                var overlay = Assert.IsType<BusyOverlayControl>(window.FindName("BusyOverlay"));
                overlay.Show("Downloading update", "Preparing…");
                overlay.IsIndeterminate = false;
                overlay.ProgressValue = 42;
                window.UpdateLayout();

                Assert.True(overlay.IsBusy);
                Assert.False(overlay.IsIndeterminate);
                Assert.Equal(42, overlay.ProgressValue);
                Assert.Equal("Cancel", Assert.IsType<Button>(overlay.FindName("CancelButton")).Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UpdateAvailableWindow_LoadsAsModelessReleaseNotification()
    {
        _wpf.Run(() =>
        {
            var window = new UpdateAvailableWindow("1.0.0", "1.1.0");
            var dismissed = 0;
            window.DismissRequested += (_, _) => dismissed++;
            try
            {
                window.Measure(new Size(510, 332));
                window.Arrange(new Rect(0, 0, 510, 332));

                Assert.False(window.ShowInTaskbar);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal("v1.0.0", Assert.IsType<TextBlock>(window.FindName("CurrentVersionText")).Text);
                Assert.Equal("v1.1.0", Assert.IsType<TextBlock>(window.FindName("LatestVersionText")).Text);
                Assert.Equal("Update", Assert.IsType<Button>(window.FindName("UpdateButton")).Content);
                Assert.Equal("Dont update now", Assert.IsType<Button>(window.FindName("DontUpdateNowButton")).Content);
            }
            finally
            {
                window.Close();
            }

            Assert.Equal(1, dismissed);
        });
    }

    [Fact]
    public void DebugWindow_ProvidesSideEffectFreeUpdateVersionPreview()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.UpdateVersionPreviewRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("UpdateVersionPreviewButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Update version", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesCurrentMapApiCapture()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.SaveMapApiRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("SaveMapApiButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Save map API", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesOneYellowFarmMoveAction()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.MoveLossFarmsTestRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("TestMoveLossFarmsButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Move red/yellow farms", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesIncomingAttackSoundTest()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.IncomingAttackSoundTestRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("TestIncomingAttackSoundButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Test incoming attack sound", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DebugWindow_ProvidesCheckCapitalAction()
    {
        _wpf.Run(() =>
        {
            var window = new FunctionTestWindow();
            try
            {
                var requested = false;
                window.CheckCapitalRequested += (_, _) => requested = true;
                var button = Assert.IsType<Button>(window.FindName("CheckCapitalButton"));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(requested);
                Assert.Equal("Check capital", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BuildingSlotsWindow_LoadsWithTheImageAndACloseButton()
    {
        _wpf.Run(() =>
        {
            var window = new BuildingSlotsWindow();
            try
            {
                window.Measure(new Size(1000, 820));
                window.Arrange(new Rect(0, 0, 1000, 820));

                var close = Assert.IsType<Button>(window.FindName("CloseButton"));
                Assert.True(close.IsCancel, "Close must stay IsCancel so Esc closes the window.");
                Assert.Equal("Close", close.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CatapultClearButton_ResetsTroopAmountsToZero()
    {
        _wpf.Run(() =>
        {
            var window = new CatapultWaveWindow(
                "Romans",
                new Dictionary<string, long> { ["Legionnaire"] = 100 });
            try
            {
                var firstAttackGrid = Assert.IsType<Grid>(window.FindName("FirstAttackTroopsGrid"));
                var wavesGrid = Assert.IsType<Grid>(window.FindName("WaveTroopsGrid"));
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("SwitchVillageButton")), "SuccessBgBrush", "SuccessBorderBrush", "SuccessTextBrush");
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("StartButton")), "SuccessBgBrush", "SuccessBorderBrush", "SuccessTextBrush");
                AssertSoftButtonColors(Assert.IsType<Button>(window.FindName("ClearButton")), "AmberBg200Brush", "WarningBorderBrush", "WarningText2Brush");
                var tabDelay = Assert.IsType<ComboBox>(window.FindName("TabOpenDelayComboBox"));
                Assert.Equal(["50 ms", "100 ms", "200 ms", "300 ms", "500 ms"], tabDelay.Items.OfType<ComboBoxItem>().Select(item => item.Content));
                Assert.Equal("100 ms", Assert.IsType<ComboBoxItem>(tabDelay.SelectedItem).Content);
                var troopInputs = firstAttackGrid.Children.OfType<TextBox>()
                    .Concat(wavesGrid.Children.OfType<TextBox>())
                    .ToArray();
                Assert.NotEmpty(troopInputs);

                foreach (var input in troopInputs)
                {
                    input.Text = "17";
                }

                Assert.IsType<Button>(window.FindName("ClearButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.All(troopInputs, input => Assert.Equal("0", input.Text));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CatapultFirstAttackTargetPickers_UseTheCurrentTribeBuildingCatalog()
    {
        _wpf.Run(() =>
        {
            var window = new CatapultWaveWindow("Romans");
            try
            {
                var firstTarget = Assert.IsType<ComboBox>(window.FindName("FirstAttackTarget1ComboBox"));
                var secondTarget = Assert.IsType<ComboBox>(window.FindName("FirstAttackTarget2ComboBox"));
                var firstRandom = Assert.IsType<RadioButton>(window.FindName("FirstAttackTarget1RandomRadioButton"));

                Assert.Contains("Main Building", firstTarget.Items.OfType<string>());
                Assert.Equal(firstTarget.Items.OfType<string>(), secondTarget.Items.OfType<string>());
                Assert.True(firstRandom.IsChecked);

                firstTarget.SelectedItem = "Main Building";
                Assert.False(firstRandom.IsChecked);

                firstRandom.IsChecked = true;
                Assert.Null(firstTarget.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VillageSettingsPanel_HidesTribeColumnWhenEveryVillageSharesOneTribe()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Egyptians"), BuildRow("Second", "Egyptians")],
            Visibility.Collapsed);
    }

    [Fact]
    public void VillageSettingsPanel_HidesTribeColumnWhenNoTribeIsKnown()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", string.Empty), BuildRow("Second", string.Empty)],
            Visibility.Collapsed);
    }

    [Fact]
    public void BuildingTemplateImportWindow_LoadsConflictManifest()
    {
        _wpf.Run(() =>
        {
            var template = new BuildingTemplate
            {
                Name = "Shared starter",
                CreatedByTribe = "Gauls",
                Rows = [new BuildingTemplateRow { Gid = 15, BuildingName = "Main Building", TargetLevel = 3 }],
            };
            var candidate = new BuildingTemplateImportCandidate(template, [], TribeMismatch: true);
            var window = new BuildingTemplateImportWindow([candidate], [template.Id]);
            try
            {
                window.Measure(new Size(900, 560));
                window.Arrange(new Rect(0, 0, 900, 560));
                window.UpdateLayout();

                var row = Assert.Single(window.Rows);
                Assert.True(row.HasConflict);
                Assert.True(row.TribeMismatch);
                Assert.Equal("Conflict · Different tribe", row.StatusText);
                Assert.Equal(BuildingTemplateImportConflictAction.ImportAsCopy, row.SelectedConflictChoice.Action);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VillageSettingsPanel_ShowsTribeColumnOnASpecialServerWithMixedTribes()
    {
        AssertTribeColumnVisibility(
            [BuildRow("Capital", "Spartans"), BuildRow("Second", "Egyptians"), BuildRow("Third", "Huns")],
            Visibility.Visible);
    }

    [Fact]
    public void CreateLossFarmListWindow_DestinationModesAreExclusiveAndEnableTheirInput()
    {
        _wpf.Run(() =>
        {
            var destination = new FarmLossDestinationOption("42", "FET", "TT", 0, 100);
            var window = new CreateLossFarmListWindow("Red farms", [destination]);
            try
            {
                var useExisting = Assert.IsType<RadioButton>(window.FindName("UseExistingRadioButton"));
                var createNew = Assert.IsType<RadioButton>(window.FindName("CreateNewRadioButton"));
                var existingLists = Assert.IsType<ComboBox>(window.FindName("ExistingListsComboBox"));
                var listName = Assert.IsType<TextBox>(window.FindName("ListNameTextBox"));

                createNew.IsChecked = true;

                Assert.False(useExisting.IsChecked);
                Assert.True(createNew.IsChecked);
                Assert.False(existingLists.IsEnabled);
                Assert.True(listName.IsEnabled);

                useExisting.IsChecked = true;

                Assert.True(useExisting.IsChecked);
                Assert.False(createNew.IsChecked);
                Assert.True(existingLists.IsEnabled);
                Assert.False(listName.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private void AssertTribeColumnVisibility(IReadOnlyList<VillageSettingsRow> rows, Visibility expected)
    {
        _wpf.Run(() =>
        {
            var panel = new Views.VillageSettingsPanel(
                rows,
                onEnabledChanged: _ => { },
                onNpcTradeChanged: _ => { },
                onHeroResourcesChanged: _ => { },
                onConstructFasterChanged: _ => { },
                onGroupsChanged: _ => { },
                onTroopSettingsRequested: _ => { },
                onSmithyUpgradeSettingsRequested: _ => { },
                onTownHallSettingsRequested: _ => { },
                onHeroResourceSettingsRequested: _ => { },
                onConstructFasterSettingsRequested: _ => { },
                onSaved: () => { });
            var column = Assert.IsType<DataGridTextColumn>(panel.FindName("TribeColumn"));
            Assert.Equal(expected, column.Visibility);
        });
    }

    private static void AssertSoftButtonColors(Button button, string backgroundKey, string borderKey, string foregroundKey)
    {
        Assert.Equal(ThemeColors.Get(backgroundKey), Assert.IsType<SolidColorBrush>(button.Background).Color);
        Assert.Equal(ThemeColors.Get(borderKey), Assert.IsType<SolidColorBrush>(button.BorderBrush).Color);
        Assert.Equal(ThemeColors.Get(foregroundKey), Assert.IsType<SolidColorBrush>(button.Foreground).Color);
    }

    private static VillageSettingsRow BuildRow(string name, string tribe) => new()
    {
        Name = name,
        PopText = "100",
        TribeText = tribe,
        KeyInfo = new VillageSettingsStore.VillageKeyInfo($"key:{name}", name, 0, 0, false),
        GroupToggles = [],
    };
}
