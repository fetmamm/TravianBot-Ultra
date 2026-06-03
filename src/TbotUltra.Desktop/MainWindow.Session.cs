using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop;

// Login / logout / account-switch / program-reset session flows, plus the busy
// overlay shown while those run. Extracted verbatim from MainWindow.xaml.cs to
// keep that file focused; same class, so this is a pure relocation with no
// behavior change.
public partial class MainWindow
{
    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteLoginFlowAsync();
    }

    private async Task ExecuteLoginFlowAsync()
    {
        if (BlockIfSessionSleeping("Login"))
        {
            return;
        }

        // Guard against re-entrancy (e.g. double-clicking Login or clicking while a login is
        // already running). The button is also disabled via ToggleUiBusy, but this is belt-and-suspenders.
        if (_loginInProgress)
        {
            AppendLog("Login already in progress. Ignoring extra click.");
            return;
        }

        _loginInProgress = true;
        var operationId = BeginOperation("Login");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        ToggleUiBusy(true);
        ShowBusyOverlay("Logging in", "Logging in and loading account data…");
        try
        {
            var options = ApplySelectedVillageToOptions(LoadBotOptions());
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            BrowserInfoTextBlock.Text = "Browser: starting";

            // Warm the captcha solver lazily, now that we know the server. Fire-and-forget and
            // self-gated (only runs for SS-Travi with captcha auto-solve enabled) so it never
            // slows login on official servers.
            _ = RunCaptchaWarmupAsync();

            await EnsureChromiumInstalledAsync();
            AppendLog("Login started.");
            // A visible browser opens as soon as login starts. Track that in a DEDICATED flag so a
            // captcha / manual-verification popup mid-login knows the window is already open (and doesn't
            // offer to open a second, conflicting verification browser). Do NOT flip
            // _browserSessionLikelyOpen here: that flag also gates background refresh and village-selection
            // operations, and turning it on before post-login analysis finishes lets those ops race the
            // login on the shared page (tab flicker). The finally block clears this flag.
            _visibleBrowserLoginInProgress = !options.Headless;
            var snapshot = await _botService.ExecuteLoginAndLoadPostLoginSnapshotAsync(
                options,
                AppendLog,
                keepBrowserOpenAfterLogin: !options.Headless,
                cancellationToken: operationToken);
            AppendLog("Login finished.");

            BrowserInfoTextBlock.Text = "Browser: idle";
            StatusTextBlock.Text = "Login completed.";
            UpdateLoginButtonsVisual(true);
            _isLoggedIn = true;
            _inboxAutoEnabled = true;
            RefreshNatarsProfileAnalyzedFromCache();
            var officialServer = IsOfficialTravianServer(options);
            ApplyPostLoginSnapshot(snapshot);
            await RefreshResourceSnapshotForUiAsync(
                options,
                operationToken,
                forceCurrentVillage: !officialServer,
                currentPageOnly: officialServer);
            if (options.PostLoginAnalyzeFarmlists)
            {
                try
                {
                    await RefreshFarmListsFromServerAsync(options, operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login farmlist analyze failed: {ex.Message}");
                }
            }

            if (options.PostLoginAnalyzeHero)
            {
                try
                {
                    await RefreshHeroStatsAsync(operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login hero analyze failed: {ex.Message}");
                }
            }

            if (options.PostLoginAnalyzeBrewery)
            {
                try
                {
                    await RefreshBreweryCelebrationStatusAsync(options, snapshot.VillageStatus, operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Post-login brewery analyze failed: {ex.Message}");
                }
            }

            var postLoginAnalysisMayNavigate =
                options.PostLoginAnalyzeFarmlists
                || options.PostLoginAnalyzeHero
                || options.PostLoginAnalyzeBrewery;
            if (!officialServer || postLoginAnalysisMayNavigate)
            {
                await _botService.NavigateToVillageResourceFieldsAsync(
                    options,
                    AppendLog,
                    GetSelectedVillageName(),
                    GetSelectedVillageUrl(),
                    cancellationToken: operationToken);
            }

            _browserSessionLikelyOpen = !options.Headless;
            CompleteOperation(operationId, operationSw, "Login completed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Login paused.";
            AppendLog("Login paused.");
        }
        catch (Exception ex)
        {
            BrowserInfoTextBlock.Text = "Browser: error";
            StatusTextBlock.Text = TryGetFriendlyLoginError(ex) ?? "Login failed.";
            _browserSessionLikelyOpen = false;
            FailOperation(operationId, operationSw, ex);
        }
        finally
        {
            _visibleBrowserLoginInProgress = false;
            HideBusyOverlay();
            ToggleUiBusy(false);
            DisposeOperationCts();
            _loginInProgress = false;
        }
    }

    // Full-window modal overlay shown while a login/logout runs (incl. account-switch auto-login). It
    // dims the window and captures all clicks, so the only thing the user can do during the operation is
    // Cancel — preventing function buttons from interfering with the in-flight work on the shared browser.
    private void ShowBusyOverlay(string title, string text)
    {
        BusyOverlay.Show(title, text);
    }

    private void HideBusyOverlay()
    {
        BusyOverlay.Hide();
    }

    private void BusyOverlay_Cancelled(object sender, EventArgs e)
    {
        // The overlay already disabled its button and showed "Cancelling…"; we just cancel the work.
        AppendLog("Cancel requested.");
        try
        {
            _loopController.CancelOperation();
        }
        catch (ObjectDisposedException)
        {
            // Operation already finished; nothing to cancel.
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockIfSessionSleeping("Logout"))
        {
            return;
        }

        var operationId = BeginOperation("Logout");
        var operationSw = Stopwatch.StartNew();
        var operationToken = _loopController.StartOperation("operation");
        ToggleUiBusy(true);
        ShowBusyOverlay("Logging out", "Logging out…");
        try
        {
            await LogoutCoreAsync(operationId, operationToken, clearSavedSession: true);
            CompleteOperation(operationId, operationSw, "Logout completed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Logout paused.";
            AppendLog("Logout paused.");
        }
        catch (Exception ex)
        {
            FailOperation(operationId, operationSw, ex);
            StatusTextBlock.Text = "Logout failed.";
        }
        finally
        {
            HideBusyOverlay();
            ToggleUiBusy(false);
            DisposeOperationCts();
        }
    }

    private async Task LogoutCoreAsync(string operationId, CancellationToken operationToken, bool clearSavedSession)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
        await EnsureChromiumInstalledAsync();
        await _botService.ExecuteLogoutAsync(options, AppendLog, operationToken);

        var account = _accountProvider.LoadAccount();
        if (clearSavedSession)
        {
            var statePath = AccountStoragePaths.BrowserStatePath(_projectRoot, account.Name);
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
                AppendLog($"Removed saved browser session for account '{account.Name}'.");
            }

            var legacyStatePath = AccountStoragePaths.LegacyBrowserStatePath(_projectRoot, account.Name);
            if (File.Exists(legacyStatePath))
            {
                File.Delete(legacyStatePath);
                AppendLog($"Removed legacy saved browser session for account '{account.Name}'.");
            }
        }

        StatusTextBlock.Text = "Logged out.";
        UpdateLoginButtonsVisual(false);
        _isLoggedIn = false;
        _browserSessionLikelyOpen = false;
        _inboxAutoEnabled = false;
        _lastResourceStatusForUi = null;
        _resourcesViewModel.ResetStorageForecasts();
        UpdateInboxButtons(0, 0);
    }

    private async void AccountsButton_Click(object sender, RoutedEventArgs e)
        => await AsyncUi.GuardAsync(AccountsButtonClickAsync, AppendLog);

    private async Task AccountsButtonClickAsync()
    {
        var previouslyActiveAccount = _accountStore.ActiveAccountName();
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var previousLoggedIn = _isLoggedIn;
        var defaultServers = await FetchDefaultServerOptionsAsync(options);
        var servers = FetchEffectiveServerOptions(defaultServers);
        var window = new AccountsWindow(_accountStore, _accountDeletionService, _serverCatalogStore, options.ServerName, options.BaseUrl, servers, defaultServers)
        {
            Owner = this,
        };
        window.ShowDialog();
        var activeAccountAfterDialog = _accountStore.ActiveAccountName();
        if (!string.Equals(previouslyActiveAccount, activeAccountAfterDialog, StringComparison.OrdinalIgnoreCase)
            && !_accountSwitchInProgress
            && !_loginInProgress)
        {
            _accountSwitchInProgress = true;
            try
            {
                await ResetForAccountSwitchAsync(options, previousLoggedIn);
                AppendLog($"Active account changed to '{activeAccountAfterDialog}'. Previous session closed and state reset.");
                ResetVillageSelectionUi();
                LoadConfigToUi();

                if (previousLoggedIn)
                {
                    // Mirror the Login button: open a fresh browser/session and log into the new account.
                    AppendLog($"Logging into '{activeAccountAfterDialog}'.");
                    await ExecuteLoginFlowAsync();
                }
                else
                {
                    StatusTextBlock.Text = $"Active account: {activeAccountAfterDialog}. Press Login to start a new session.";
                }
            }
            finally
            {
                _accountSwitchInProgress = false;
            }

            return;
        }

        LoadConfigToUi();
    }

    private async void AccountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelectionChange)
        {
            return;
        }

        if (AccountComboBox.SelectedItem is not AccountEntry selected)
        {
            return;
        }

        var current = _accountStore.ActiveAccountName();
        if (string.Equals(current, selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // A switch (logout → close → reopen + auto-login) is a heavy async flow that drives the shared
        // browser. If the user changes the picker again before it finishes, a second handler would run
        // concurrently and the two would fight over the same browser (tab flicker). Ignore further changes
        // until the current switch/login completes, and revert the picker to the still-active account.
        if (_accountSwitchInProgress || _loginInProgress)
        {
            AppendLog("Account switch/login already in progress. Ignoring account change until it finishes.");
            RefreshAccountPicker();
            return;
        }

        _accountSwitchInProgress = true;
        try
        {
            // Capture the previous account's options before switching so we can log it out cleanly.
            var previousOptions = ApplySelectedVillageToOptions(LoadBotOptions());
            var previousLoggedIn = _isLoggedIn;

            await ResetForAccountSwitchAsync(previousOptions, previousLoggedIn);

            _accountStore.SetActive(selected.Name);
            AppendLog($"Active account changed to '{selected.Name}'. Previous session closed and state reset.");
            ResetVillageSelectionUi();
            SyncServerFromActiveAccount();
            LoadConfigToUi();

            if (previousLoggedIn)
            {
                // Mirror the Login button: open a fresh browser/session and log into the new account.
                AppendLog($"Logging into '{selected.Name}'.");
                await ExecuteLoginFlowAsync();
            }
            else
            {
                StatusTextBlock.Text = $"Active account: {selected.Name}. Press Login to start a new session.";
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not change active account: {ex.Message}");
            RefreshAccountPicker();
        }
        finally
        {
            _accountSwitchInProgress = false;
        }
    }

    private void ResetVillageSelectionUi()
    {
        ApplyVillagePickerItems(
            new[]
            {
                new VillageSelectionItem { Name = "-", Url = string.Empty },
            },
            preferredVillageName: "-",
            fallbackVillageName: null);
    }

    // ResetVillageSelectionUi() goes through ApplyVillagePickerItems(), which deliberately REFUSES to
    // blank a populated picker (guards against transient empty status updates mid-navigation). That makes
    // it a no-op on account switch, leaving the previous account's village selected — which then leaks
    // into the new account's login as a bogus target village (navigating to a village that doesn't exist
    // on the new server). This forces the picker + dashboard list back to the empty placeholder.
    private void ForceClearVillageSelectionUi()
    {
        _suppressVillageSelectionChange = true;
        try
        {
            var placeholder = new[] { new VillageSelectionItem { Name = "-", Url = string.Empty } };
            VillageComboBox.ItemsSource = placeholder;
            VillageComboBox.SelectedItem = placeholder[0];
            DashboardVillageList.ItemsSource = Array.Empty<VillageSelectionItem>();
        }
        finally
        {
            _suppressVillageSelectionChange = false;
        }
    }

    private async void ResetProgramButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = AppDialog.Show(
            "This will restart the program: stop running operations, clear the queue, close the browser session, and reset to the just-started state.\n\nThe program stays open and you can press Login to start a new session.\n\nContinue?",
            "Reset program",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ResetProgramInternalAsync();
    }

    private async Task ResetProgramInternalAsync()
    {
        try
        {
            await StopAllAutomationAndWaitAsync();

            // Close the shared browser session so the program returns to its just-started state.
            // This disposes Chromium but keeps the saved auth state, so the user can simply press
            // Login again (no need to re-enter credentials).
            try
            {
                await _botService.ShutdownAsync(AppendLog);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not close browser during reset: {ex.Message}");
            }

            ClearAccountScopedUiState();
            StatusTextBlock.Text = "Program reset. Press Login to start a new session.";
            AppendLog("Program reset completed. Browser session closed, internal state and queue cleared. Press Login to start again.");
        }
        catch (Exception ex)
        {
            AppendLog($"Program reset failed: {ex.Message}");
        }
    }

    private async Task StopAllAutomationAndWaitAsync()
    {
        _loopController.RequestLoopStop();
        _loopController.RequestQueueStop();
        _loopController.CancelOperation();
        _loopController.CancelAutoQueueRun();
        _loopController.CancelLoop();
        _loopController.CancelVillageSwitch();

        var stopDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < stopDeadline)
        {
            if (!_uiBusy
                && !_autoQueueRunning
                && !_resourceSnapshotRefreshRunning
                && (_loopTask is null || _loopTask.IsCompleted))
            {
                break;
            }

            await Task.Delay(120);
        }
    }

    private void ClearAccountScopedUiState()
    {
        _botService.ClearQueue();
        _resourceClickCooldownBySlot.Clear();
        _resourceLastQueuedTargetBySlot.Clear();
        _resourcesViewModel.ClearPendingTargets();
        _buildingClickCooldownBySlot.Clear();
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildQueueActiveCount = 0;
        _buildQueueRemainingSeconds = -1;
        _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
        _lastBuildingStatus = null;
        _lastResourceStatusForUi = null;

        SetResourceRows([]);
        _resourcesViewModel.ResetStorageForecasts();
        _buildingRows.Clear();
        _demolishableBuildings.Clear();
        ForceClearVillageSelectionUi();
        BuildQueueStatusTextBlock.Text = "Build queue: idle";

        // Return login/session state to startup: not logged in, browser closed, inbox idle.
        _isLoggedIn = false;
        _browserSessionLikelyOpen = false;
        _inboxAutoEnabled = false;
        UpdateLoginButtonsVisual(false);
        UpdateInboxButtons(0, 0);

        RefreshQueueUi();
        UpdateExecutionStateIndicator();
    }

    // Switching the active account mid-session must behave like a fresh start: stop any running
    // automation, log out + close the old browser session, and clear all account-scoped cache so
    // stale buildings/villages/login state from the previous account never carry over. Closing the
    // worker session also guarantees the next login opens a brand-new browser and runs the full
    // login flow (the fresh session cache forces a real logged-in check).
    private async Task ResetForAccountSwitchAsync(BotOptions previousOptions, bool previousLoggedIn)
    {
        // Disable all background session work BEFORE anything else. While _isLoggedIn /
        // _browserSessionLikelyOpen are still true, the ~16s resource-refresh tick (and inbox checks)
        // can slip onto the session gate right after logout and silently log the OLD account back in.
        // Flipping these first makes ShouldRunBackgroundResourceSnapshotRefresh() bail; the wait below
        // then drains anything already in flight.
        _isLoggedIn = false;
        _browserSessionLikelyOpen = false;
        _inboxAutoEnabled = false;
        await StopAllAutomationAndWaitAsync();

        if (previousLoggedIn)
        {
            try
            {
                await _botService.ExecuteLogoutAsync(previousOptions, AppendLog, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not log out previous account (continuing): {ex.Message}");
            }
        }

        try
        {
            await _botService.ShutdownAsync(AppendLog);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not close browser during account switch: {ex.Message}");
        }

        ClearPersistedAccountScopedConfig();
        ClearAccountScopedUiState();
    }

    // bot.json is shared across accounts, but a handful of settings point at specific villages or
    // farm lists (by name, URL or id). Left in place they make the next login navigate to a village
    // that does not exist on the new account — which silently logs the fresh session out. Remove
    // them on every account switch so the new account is not contaminated by the previous one.
    private void ClearPersistedAccountScopedConfig()
    {
        string[] accountScopedKeys =
        {
            BotOptionPayloadKeys.TargetVillageName,
            BotOptionPayloadKeys.TargetVillageUrl,
            BotOptionPayloadKeys.ReinforcementsTargetVillageName,
            BotOptionPayloadKeys.ReinforcementsSourceVillageNames,
            BotOptionPayloadKeys.ResourceTransferTargetVillageName,
            BotOptionPayloadKeys.ResourceTransferSourceVillageNames,
            BotOptionPayloadKeys.ContinuousFarmListNames,
            BotOptionPayloadKeys.ContinuousFarmListIds,
        };

        try
        {
            var config = _botConfigStore.Load();
            var changed = false;
            foreach (var key in accountScopedKeys)
            {
                if (config.Remove(key))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                _botConfigStore.Save(config);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not clear previous account's village/farm-list settings: {ex.Message}");
        }
    }
}
