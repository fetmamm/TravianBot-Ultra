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
    // Login button clicked
    private async void LoginButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(ExecuteLoginFlowAsync);

    // Login function as the button is clicked
    private async Task ExecuteLoginFlowAsync()
    {
        AppendLog("[login] ***** Login started. *****");
        if (BlockIfSessionSleeping("Login"))
        {
            return;
        }

        // If session pacing is in a planned off-hours / daily-limit window, logging in would run the whole
        // login + analyze stack only to immediately sleep and log back out. Go straight to sleep instead;
        // the user can press the pacing Run-now (play) button to override and log in normally.
        if (TryEnterPlannedSleepInsteadOfLogin())
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
            // Login must NOT auto-switch villages: read where Travian actually lands and mark that as the
            // active working village. Injecting the (possibly stale) selected village here made the bot
            // navigate away from the landing village to the capital/selected one. The dropdown is synced
            // to the real landing village after the snapshot; use "Switch village" to move on purpose.
            var options = LoadBotOptions();
            AppendLog($"[{operationId}] INFO server={options.ServerName}, headless={options.Headless}");
            BrowserInfoTextBlock.Text = "Browser: starting";

            await EnsureChromiumInstalledAsync();
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

            BrowserInfoTextBlock.Text = "Browser: idle";
            StatusTextBlock.Text = "Login completed.";
            UpdateLoginButtonsVisual(true);
            _isLoggedIn = true;
            _inboxAutoEnabled = true;
            // Bring in any village buildings/fields remembered from a previous session so the dropdown can
            // show them immediately; the fresh post-login read then updates the landing village.
            LoadVillageCacheForActiveAccount();
            LoadHeroHomeVillageForActiveAccount();
            // Check if server is official
            var officialServer = IsOfficialTravianServer(options);
            ApplyPostLoginSnapshot(snapshot);
            await RefreshResourceSnapshotForUiAsync(
                options,
                operationToken,
                forceCurrentVillage: !officialServer,
                currentPageOnly: officialServer);
            // Hero inventory is read during the post-login snapshot (step 1). Run the hero ATTRIBUTES
            // analyze right after it (step 2) — before farmlists — so the hero panel + home-village marker
            // update as early as possible.
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

            var newVillageAnalysisNavigated = false;
            if (options.PostLoginAnalyzeNewVillages)
            {
                newVillageAnalysisNavigated = await AnalyzeNewVillagesAfterLoginAsync(
                    options,
                    snapshot.VillageStatus.Villages,
                    operationToken);
            }

            var postLoginAnalysisMayNavigate =
                options.PostLoginAnalyzeFarmlists
                || options.PostLoginAnalyzeHero
                || options.PostLoginAnalyzeBrewery
                || newVillageAnalysisNavigated;
            if (!officialServer || postLoginAnalysisMayNavigate)
            {
                await _botService.NavigateToVillageResourceFieldsAsync(
                    options,
                    AppendLog,
                    GetSelectedVillageName(),
                    GetSelectedVillageUrl(),
                    cancellationToken: operationToken);
                SetActiveWorkingVillage(
                    GetSelectedVillageKey(),
                    GetSelectedVillageName());
            }

            _browserSessionLikelyOpen = !options.Headless;
            NotifySessionPacingOnlineStarted();
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
        AppendLog("[login] ***** Login finished. *****");
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
// Logout knapp klickas
    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(LogoutButtonClickAsync);

    private async Task LogoutButtonClickAsync()
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
        NotifySessionPacingOnlineStopped();
        _lastResourceStatusForUi = null;
        _resourcesViewModel.ResetStorageForecasts();
        UpdateInboxButtons(0, 0);
    }

    private async void AccountsButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(AccountsButtonClickAsync);

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
                RecoverAndRefreshActiveAccountQueue();
                AppendLog($"Active account changed to '{activeAccountAfterDialog}'. Previous session closed and state reset.");
                ResetVillageSelectionUi();
                LoadConfigToUi();
                ConfigureSessionPacerFromConfig(reloadRuntime: true);

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
        => await GuardUiAsync(() => AccountComboBoxSelectionChangedAsync(sender, e));

    private async Task AccountComboBoxSelectionChangedAsync(object sender, SelectionChangedEventArgs e)
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
            RecoverAndRefreshActiveAccountQueue();
            AppendLog($"Active account changed to '{selected.Name}'. Previous session closed and state reset.");
            ResetVillageSelectionUi();
            SyncServerFromActiveAccount();
            LoadConfigToUi();
            ConfigureSessionPacerFromConfig(reloadRuntime: true);

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
        => await GuardUiAsync(ResetProgramButtonClickAsync);

    private async Task ResetProgramButtonClickAsync()
    {
        var answer = AppDialog.Show(
            "This will restart the browser session and stop running operations.\n\nYour account, settings, queue, cached villages, and saved login are kept. The program stays open and you can press Login to start a fresh browser session.\n\nContinue?",
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

            _isLoggedIn = false;
            _browserSessionLikelyOpen = false;
            _inboxAutoEnabled = false;
            NotifySessionPacingOnlineStopped();
            UpdateLoginButtonsVisual(false);
            UpdateInboxButtons(0, 0);
            SetLoopIndicator(false);
            StartLoopButton.Content = "Start bot";
            StartLoopButton.IsEnabled = true;
            _activeAutomationTaskName = null;
            _activeFunctionDisplayName = null;

            var recovered = _botService.ResetOrphanedRunningQueueItems();
            if (recovered > 0)
            {
                AppendLog($"Recovered {recovered} queue item(s) from Running to Pending after browser session reset.");
            }

            RefreshQueueUi();
            UpdateExecutionStateIndicator();
            StatusTextBlock.Text = "Browser session reset. Press Login to start a new session.";
            AppendLog("Browser session reset completed. Account, settings, queue, cached villages, and saved login were kept.");
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
        // Also abort session-scoped refreshes (post-task/manual status reads) that previously ran
        // with CancellationToken.None and could outlive the drain below while holding the session gate.
        _loopController.CancelSessionScope();

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

            await Task.Delay(Random.Shared.Next(150, 350)); // Random wait
        }
    }

    private void ClearAccountScopedUiState(bool clearQueue)
    {
        if (clearQueue)
        {
            _botService.ClearQueue();
        }
        _resourceClickCooldownBySlot.Clear();
        _collectTasksLastQueuedAtByVillage.Clear();
        _resourceLastQueuedTargetBySlot.Clear();
        _resourcesViewModel.ClearPendingTargets();
        _resourcesViewModel.InfoText = "Resources not loaded yet.";
        _buildingClickCooldownBySlot.Clear();
        _buildingLastQueuedTargetBySlot.Clear();
        _buildingLastQueuedConstructBySlot.Clear();
        _buildingDemolishingSlots.Clear();
        _buildQueueActiveCount = 0;
        _buildQueueRemainingSeconds = -1;
        _buildQueueReachedZeroPendingCompletion = false;
        _activeVillageResourceMaxLevel = NonCapitalResourceMaxLevel;
        _lastBuildingStatus = null;
        _lastResourceStatusForUi = null;
        _uiQueueSnapshot = null;
        _uiQueueSnapshotAtUtc = DateTimeOffset.MinValue;
        _pendingQueueUiSelectId = null;
        _travianBuildQueueRows.Clear();
        _travianSmithyQueueRows.Clear();
        _estimateAlarmedKeys.Clear();
        _serverSpeedAlarmRaised = false;

        SetResourceRows([]);
        _resourcesViewModel.ResetStorageForecasts();
        _lastDisplayedVillageSignature = null;
        // Drop the cached village enabled-state so the next account reloads its own villages.json
        // (the file itself is kept — it is exactly the per-account memory we want to persist).
        _villageSettingsStore.InvalidateCache();
        _travcoListStore.InvalidateCache();
        // Drop the in-memory per-village buildings/fields cache so the next account doesn't show the
        // previous account's villages. The on-disk village_cache.json per account is kept.
        _villageStatusCacheByName.Clear();
        _buildingRows.Clear();
        _buildingCatalogOptions.Clear();
        _demolishableBuildings.Clear();
        BuildingsInfoTextBlock.Text = "Buildings not loaded yet.";
        VillagesInfoTextBlock.Text = "Villages: 0";
        ForceClearVillageSelectionUi();
        BuildQueueStatusTextBlock.Text = "Build queue: idle";

        _heroViewModel.ResetRuntimeState();
        _heroHomeVillageName = null;
        _heroIsAway = false;
        _heroIsDead = false;
        _heroIsReviving = false;

        _troopTrainingViewModel.ResetRuntimeState();
        _smithyUpgradeRemainingSeconds.Clear();
        _smithyUpgradeStatusRefreshRunning = false;
        _pendingSmithyUpgradeStatusBuildings = null;
        _knownBrewerySlotByVillage.Clear();

        foreach (var row in _farmLists)
        {
            row.PropertyChanged -= FarmListStatusRow_PropertyChanged;
        }
        _farmLists.Clear();
        EnsureFarmListPlaceholderRow();
        _analyzedFarmCoordinates.Clear();
        _farmListCapacitiesByName.Clear();
        _lastFarmListsAnalysisAt = DateTimeOffset.MinValue;
        _farmingFeaturesAvailable = true;
        if (FarmingStatusTextBlock is not null)
        {
            FarmingStatusTextBlock.Text = "No farm lists loaded. Click Analyze Farmlists.";
        }

        foreach (var village in _resourceTransferVillages)
        {
            village.PropertyChanged -= ResourceTransferVillage_PropertyChanged;
        }
        _resourceTransferVillages.Clear();
        ResourceTransferTargetVillageComboBox.SelectedItem = null;
        ResourceTransferStatusTextBlock.Text = "No villages loaded.";

        foreach (var village in _reinforcementVillages)
        {
            village.PropertyChanged -= ReinforcementVillage_PropertyChanged;
        }
        _reinforcementVillages.Clear();
        _reinforcementSourceVillages.Clear();
        ClearReinforcementTroopRules();
        _configuredReinforcementTroopRules = [];
        ReinforcementTargetVillageComboBox.SelectedItem = null;
        ReinforcementStatusTextBlock.Text = "No villages loaded.";

        _activeWorkingVillageKey = null;
        _activeWorkingVillageName = null;
        lock (_pendingSwitchVillageLock)
        {
            _pendingSwitchVillageName = null;
            _pendingSwitchVillageUrl = null;
        }
        _continuousGroupRotationVillageKeys.Clear();
        _defaultEnabledGroupKeys.Clear();

        _troopsBlockedReasonKey = null;
        _troopsBlockedReasonText = null;
        _troopsBlockedPreviouslyEnabled = false;
        _farmingBlockedReasonKey = null;
        _farmingBlockedReasonText = null;
        _farmingBlockedPreviouslyEnabled = false;
        _heroBlockedReasonKey = null;
        _heroBlockedReasonText = null;
        _heroBlockedPreviouslyEnabled = false;
        _breweryBlockedReasonKey = null;
        _breweryBlockedReasonText = null;
        _breweryBlockedPreviouslyEnabled = false;

        _lastContinuousInboxCheckUtc = DateTimeOffset.MinValue;
        _lastContinuousBrowserActivityUtc = DateTimeOffset.MinValue;
        _lastContinuousKeepAliveFailureUtc = DateTimeOffset.MinValue;
        _inlineWaitUntilUtc = DateTimeOffset.MinValue;
        _manualFarmSessionExecutionCount = 0;
        UpdateManualFarmingExecutionCounter();
        _npcTradeSessionCount = 0;
        _npcTradeTroopSessionCount = 0;
        _npcTradeBuildingSessionCount = 0;
        _pendingManualOperationId = null;
        _operationNamesById.Clear();
        _activeManualExecution = null;
        _activeAutomationTaskName = null;
        _activeFunctionDisplayName = null;

        // Return login/session state to startup: not logged in, browser closed, inbox idle.
        _isLoggedIn = false;
        _browserSessionLikelyOpen = false;
        _inboxAutoEnabled = false;
        NotifySessionPacingOnlineStopped();
        UpdatePlusInfo(null);
        UpdateGoldClubInfo(null);
        TribeInfoTextBlock.Text = "-";
        UpdateLoginButtonsVisual(false);
        UpdateInboxButtons(0, 0);

        // A reset/account switch stops automation via RequestLoopStop/RequestQueueStop. Those flags
        // are only cleared when a NEW run starts, so without this the state indicator would keep
        // reading "paused" even though nothing is running and the user is logged out. Clearing them
        // here returns the idle/just-started state correctly.
        _loopController.ClearLoopStopRequest();
        _loopController.ClearQueueStopRequest();

        RefreshQueueUi();
        UpdateExecutionStateIndicator();
    }

    private void RecoverAndRefreshActiveAccountQueue()
    {
        var recovered = _botService.ResetOrphanedRunningQueueItems();
        if (recovered > 0)
        {
            AppendLog($"Recovered {recovered} queue item(s) for '{_accountStore.ActiveAccountName()}' from Running to Pending.");
        }

        RefreshQueueUi();
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
        NotifySessionPacingOnlineStopped();
        await StopAllAutomationAndWaitAsync();

        if (previousLoggedIn)
        {
            try
            {
                // Time-boxed: if a stuck operation still holds the session gate, the logout must not
                // hang the whole account switch — ShutdownAsync below force-closes the browser anyway.
                using var logoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _botService.ExecuteLogoutAsync(previousOptions, AppendLog, logoutCts.Token);
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

        // Account switching must clear only in-memory/UI state. Each account's queue.json is preserved
        // and becomes visible again when that account is selected later in the same app session.
        ClearAccountScopedUiState(clearQueue: false);

        // bot.json is global, so the previous account's village/farm-list pointers would otherwise leak
        // into the next account and make its first login navigate to a non-existent village. Strip them
        // here; the new account's own settings.json overlay re-supplies its values on the next load.
        ClearPersistedAccountScopedConfig();
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
            BotOptionPayloadKeys.ReinforcementsSendIntervalHours,
            BotOptionPayloadKeys.ReinforcementsSendVariationPercent,
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
