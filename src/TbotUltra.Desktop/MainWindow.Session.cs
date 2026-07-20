using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TbotUltra.Core.Accounts;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Desktop.Views;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Services;

namespace TbotUltra.Desktop;

// Login / logout / account-switch / program-reset session flows, plus the busy
// overlay shown while those run. Extracted verbatim from MainWindow.xaml.cs to
// keep that file focused; same class, so this is a pure relocation with no
// behavior change.
public partial class MainWindow
{
    private AccountEntry? _pendingProxyChangeAtSleep;

    // Login button clicked
    private async void LoginButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(ExecuteLoginFlowAsync);

    // Login function as the button is clicked
    private async Task ExecuteLoginFlowAsync()
    {
        AppendLog("[login] ***** Login started. *****");
        if (BlockIfActiveAccountOnHold("Login"))
        {
            AppendLog("[login] ***** Login finished. *****");
            return;
        }

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

        if (!PrepareProxyPlanForLogin())
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
        var retryAfterLanguageFix = false;
        ToggleUiBusy(true);
        ShowBusyOverlay("Logging in", "Logging in and loading account data…");
        try
        {
            // Login must NOT auto-switch villages: read where Travian actually lands and mark that as the
            // active working village. Injecting the (possibly stale) selected village here made the bot
            // navigate away from the landing village to the capital/selected one. The dropdown is synced
            // to the real landing village after the snapshot; use "Switch village" to move on purpose.
            var options = LoadValidatedActiveAccountOptions();
            AppendLog($"[{operationId}] INFO server={options.ServerName}");
            BrowserInfoTextBlock.Text = "Browser: starting";

            await EnsureChromiumInstalledAsync();
            // Quick re-login: the full post-login stack (snapshot + analyzes) was completed for this
            // account only minutes ago, and nothing meaningful changes server-side that fast. Log in,
            // confirm the session, restore the persisted caches — done.
            if (IsQuickReloginWindowActive(out var minutesSinceFullLogin))
            {
                AppendLog($"[{operationId}] Quick re-login: full post-login stack ran {minutesSinceFullLogin:F0} min ago (<{QuickReloginWindowMinutes} min) — logging in without analyzes.");
                await _botService.ExecuteLoginAsync(options, AppendLog, keepBrowserOpenAfterLogin: true, operationToken);

                BrowserInfoTextBlock.Text = "Browser: idle";
                StatusTextBlock.Text = "Login completed (quick re-login).";
                UpdateLoginButtonsVisual(true);
                _isLoggedIn = true;
                _inboxAutoEnabled = true;
                LoadVillageCacheForActiveAccount();
                LoadHeroHomeVillageForActiveAccount();

                // Fill the dashboard (resources, villages, adventure count) from the landing page
                // BEFORE the busy overlay closes, so the UI is not half-empty when it appears. Cheap:
                // current-page read on official, no extra navigation beyond the login landing.
                try
                {
                    var quickOfficialServer = IsOfficialTravianServer(options);
                    await RefreshResourceSnapshotForUiAsync(
                        options,
                        operationToken,
                        forceCurrentVillage: !quickOfficialServer,
                        currentPageOnly: quickOfficialServer);
                    await ReadHeroHpFromCurrentPageForUiAsync(options, operationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UnexpectedTravianLanguageException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppendLog($"Quick re-login UI refresh failed (continuing): {ex.Message}");
                }

                // Quick re-login skips the full analyze stack, so the overview cards render from the
                // persisted per-village cache. Re-apply the indicators explicitly so expired timers
                // (constructions/trainings finished while the app was closed) are dropped right away.
                RefreshVillageActivityIndicatorsOnDashboard();

                // Apply the selected village's cached state to all detail panels — the same routine a
                // manual village re-selection runs. Without it the panels stay on startup defaults
                // until the user pokes the village picker.
                if (VillageComboBox.SelectedItem is VillageSelectionItem quickSelectedVillage)
                {
                    ShowSelectedVillageFromCache(quickSelectedVillage);
                }

                _browserSessionLikelyOpen = true;
                PrepareConstructionLoginFill();
                NotifySessionPacingOnlineStarted();
                CompleteOperation(operationId, operationSw, "Login completed (quick re-login).");
                return;
            }

            var snapshot = await _botService.ExecuteLoginAndLoadPostLoginSnapshotAsync(
                options,
                AppendLog,
                keepBrowserOpenAfterLogin: true,
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
            try
            {
                await ReadHeroHpFromCurrentPageForUiAsync(options, operationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"Post-login Hero HP refresh failed (continuing): {ex.Message}");
            }
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

            _browserSessionLikelyOpen = true;
            PrepareConstructionLoginFill();
            NotifySessionPacingOnlineStarted();
            // Anchor for the quick re-login window: only a COMPLETED full stack counts.
            PersistLastFullPostLoginTimestamp();
            CompleteOperation(operationId, operationSw, "Login completed.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Login paused.";
            AppendLog("Login paused.");
            CompleteOperation(operationId, operationSw, "Login canceled.");
        }
        catch (UnexpectedTravianLanguageException ex)
        {
            BrowserInfoTextBlock.Text = "Browser: language required";
            StatusTextBlock.Text = "Travian language must be set to English.";
            CompleteOperation(operationId, operationSw, "Login paused until Travian language is English.");
            HideBusyOverlay();
            ToggleUiBusy(false);
            retryAfterLanguageFix = await HandleUnexpectedTravianLanguageAsync(ex);
        }
        catch (AccountAccessException ex)
        {
            BrowserInfoTextBlock.Text = "Browser: account stopped";
            StatusTextBlock.Text = "Automation stopped for this account. Manual review is required.";
            await HoldAccountAutomationAsync(ex);
            CompleteOperation(operationId, operationSw, "Account automation stopped for manual review.");
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
            HideBusyOverlay();
            ToggleUiBusy(false);
            RefreshAccountHoldUi();
            DisposeOperationCts();
            _loginInProgress = false;
        }
        AppendLog("[login] ***** Login finished. *****");
        if (retryAfterLanguageFix)
        {
            AppendLog("[language] Travian language verified. Restarting login flow.");
            _ = Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await ExecuteLoginFlowAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[language] Login retry failed: {ex.Message}");
                }
            });
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

        // A browser download owns the overlay while it runs, so Cancel has to reach it too.
        _chromiumInstallCts?.Cancel();

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
        AppendLog($"[{operationId}] INFO server={options.ServerName}");
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

        ResetLoggedOutUiState();
    }

    private void ResetLoggedOutUiState()
    {
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

    private async Task CloseBrowserForSleepAsync(string operationId)
    {
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        AppendLog($"[{operationId}] INFO server={options.ServerName}");

        try
        {
            await _botService.SaveBrowserStateAsync(AppendLog);
        }
        catch (Exception ex)
        {
            AppendLog($"[pacing] browser state save before sleep failed; closing with the last saved state: {ex.Message}");
        }

        try
        {
            await _botService.ShutdownAsync(AppendLog);
        }
        finally
        {
            ResetLoggedOutUiState();
        }
    }

    private async void AccountsButton_Click(object sender, RoutedEventArgs e)
        => await GuardUiAsync(AccountsButtonClickAsync);

    private async Task AccountsButtonClickAsync()
    {
        var previouslyActiveAccount = _accountStore.ActiveAccountName();
        var activeAccountBeforeDialog = FindAccount(previouslyActiveAccount);
        var options = ApplySelectedVillageToOptions(LoadBotOptions());
        var previousLoggedIn = _isLoggedIn;
        var defaultServers = await FetchDefaultServerOptionsAsync(options);
        var servers = FetchEffectiveServerOptions(defaultServers);
        AccountsWindow window;
        try
        {
            window = new AccountsWindow(_accountStore, _accountDeletionService, _serverCatalogStore, options.ServerName, options.BaseUrl, servers, defaultServers)
            {
                Owner = this,
            };
        }
        catch (Exception ex)
        {
            AppendLog($"[ui] Accounts window could not initialize normally: {ex.Message}");
            window = new AccountsWindow(_accountStore, _accountDeletionService, _serverCatalogStore, options.ServerName, options.BaseUrl, Array.Empty<ServerOption>(), Array.Empty<ServerOption>())
            {
                Owner = this,
            };
        }

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
                RefreshAfterActiveAccountChanged(activeAccountAfterDialog);

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

        var activeAccountAfterEdit = FindAccount(activeAccountAfterDialog);
        if (activeAccountBeforeDialog is not null
            && activeAccountAfterEdit is not null
            && string.Equals(previouslyActiveAccount, activeAccountAfterDialog, StringComparison.OrdinalIgnoreCase)
            && ProxyConfigurationChanged(activeAccountBeforeDialog, activeAccountAfterEdit)
            && _isLoggedIn)
        {
            // Keep all non-proxy account edits, but restore the proxy currently used by the running browser.
            // BotTaskRunner otherwise sees the new proxy fingerprint on the next task and replaces Chromium
            // immediately, before a controlled logout/login can run.
            var runtimeAccount = CloneAccount(activeAccountAfterEdit);
            runtimeAccount.ProxyEnabled = activeAccountBeforeDialog.ProxyEnabled;
            runtimeAccount.ProxyServer = activeAccountBeforeDialog.ProxyServer;
            runtimeAccount.NeverUseOwnIp = activeAccountBeforeDialog.NeverUseOwnIp;
            _accountStore.SaveAccount(runtimeAccount, setActive: false);

            var choice = AppDialog.ShowCustom(
                this,
                "Proxy settings changed for the active account. When should the new proxy be activated?\n\n" +
                "Relogin now safely stops automation, logs out, restarts the browser after a 5–20 second delay, and resumes the previous run state.\n\n" +
                "Next sleep keeps the current browser and proxy unchanged until the next session sleep (recommended).",
                "Apply proxy change",
                [("Relogin now", MessageBoxResult.Yes), ("Next sleep", MessageBoxResult.No), ("Cancel change", MessageBoxResult.Cancel)],
                MessageBoxImage.Question,
                MessageBoxResult.No,
                MessageBoxResult.Cancel,
                MessageBoxResult.No);

            if (choice == MessageBoxResult.Yes)
            {
                _pendingProxyChangeAtSleep = null;
                await ApplyProxyChangeWithImmediateReloginAsync(activeAccountAfterEdit, options);
            }
            else if (choice == MessageBoxResult.No)
            {
                _pendingProxyChangeAtSleep = CloneAccount(activeAccountAfterEdit);
                AppendLog("[proxy-change] new proxy scheduled for next session sleep; current browser remains unchanged.");
                StatusTextBlock.Text = "Proxy change scheduled for next sleep.";
            }
            else
            {
                _pendingProxyChangeAtSleep = null;
                AppendLog("[proxy-change] proxy change cancelled; current proxy kept.");
            }
        }

        LoadConfigToUi();
    }

    private AccountEntry? FindAccount(string accountName) => _accountStore.ListAccounts()
        .FirstOrDefault(account => string.Equals(account.Name, accountName, StringComparison.OrdinalIgnoreCase));

    private static bool ProxyConfigurationChanged(AccountEntry before, AccountEntry after) =>
        before.ProxyEnabled != after.ProxyEnabled
        || before.NeverUseOwnIp != after.NeverUseOwnIp
        || !string.Equals(before.ProxyServer.Trim(), after.ProxyServer.Trim(), StringComparison.OrdinalIgnoreCase);

    private static AccountEntry CloneAccount(AccountEntry source) => new()
    {
        Name = source.Name,
        Username = source.Username,
        Password = source.Password,
        ServerName = source.ServerName,
        ServerUrl = source.ServerUrl,
        ProxyEnabled = source.ProxyEnabled,
        ProxyServer = source.ProxyServer,
        NeverUseOwnIp = source.NeverUseOwnIp,
        IsActive = source.IsActive,
    };

    private async Task<bool> ApplyProxyChangeWithImmediateReloginAsync(
        AccountEntry changedAccount,
        BotOptions previousOptions,
        bool? resumeContinuousLoopOverride = null)
    {
        if (_accountSwitchInProgress || _loginInProgress)
        {
            AppendLog("[proxy-change] immediate relogin skipped because another session transition is active.");
            _pendingProxyChangeAtSleep = CloneAccount(changedAccount);
            return false;
        }

        _accountSwitchInProgress = true;
        var resumeContinuousLoop = resumeContinuousLoopOverride
            ?? (_loopTask is not null && !_loopTask.IsCompleted);
        var resumeAutoQueue = _autoQueueRunning;
        try
        {
            AppendLog("[proxy-change] controlled relogin starting; stopping active automation.");
            _isLoggedIn = false;
            _browserSessionLikelyOpen = false;
            _inboxAutoEnabled = false;
            NotifySessionPacingOnlineStopped();
            await StopAllAutomationAndWaitAsync();

            try
            {
                using var logoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _botService.ExecuteLogoutAsync(previousOptions, AppendLog, logoutCts.Token);
            }
            catch (Exception ex)
            {
                AppendLog($"[proxy-change] logout failed; continuing with browser shutdown: {ex.Message}");
            }

            await _botService.ShutdownAsync(AppendLog);
            _accountStore.SaveAccount(changedAccount, setActive: false);
            RefreshAccountPicker();
            UpdateProxyStatus(changedAccount);

            var delaySeconds = Random.Shared.Next(5, 21);
            AppendLog($"[proxy-change] new proxy saved; waiting {delaySeconds}s before fresh browser login.");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            await EnsureLoginCompletedAsync("proxy-change");

            if (!_isLoggedIn)
            {
                AppendLog("[proxy-change] relogin did not complete; automation remains stopped for safety.");
                return false;
            }

            // Restore exactly what was running before the change: the continuous loop if it was running,
            // otherwise the auto-queue if that was. If the bot was paused/idle, both stay off. The idle
            // check keeps a login that another trigger already resumed from being started a second time.
            var loopIdle = _loopTask is null || _loopTask.IsCompleted;
            if (resumeContinuousLoop && loopIdle)
            {
                StartContinuousLoopRunner();
            }
            else if (resumeAutoQueue && !_autoQueueRunning && loopIdle)
            {
                _ = TriggerQueueAutoRunAsync();
            }

            AppendLog("[proxy-change] fresh browser login completed with new proxy; previous run state restored.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[proxy-change] controlled relogin failed: {ex.Message}");
            return false;
        }
        finally
        {
            _accountSwitchInProgress = false;
        }
    }

    // How long a controlled relogin waits for a login that another trigger already started. Login itself
    // waits up to 180s for confirmation, so this has to outlast that before giving up.
    private static readonly TimeSpan InFlightLoginWaitTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan InFlightLoginPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs the login flow for a controlled relogin (proxy change / recovery) and does not return until the
    /// session is actually logged in or the attempt is really over.
    ///
    /// ExecuteLoginFlowAsync silently ignores its call when a login is already running. When the browser has
    /// just been torn down, another automatic trigger can win that race by a second or two — the old code
    /// then read _isLoggedIn while that other login was still opening the lobby, concluded the relogin had
    /// failed, and left the bot logged out-looking with all automation stopped even though the login went on
    /// to succeed. Wait for whichever login is in flight before judging the result.
    /// </summary>
    private async Task EnsureLoginCompletedAsync(string source)
    {
        await ExecuteLoginFlowAsync();
        if (_isLoggedIn || !_loginInProgress)
        {
            return;
        }

        AppendLog($"[{source}] a login started by another trigger is already running; waiting for it to finish.");
        var waited = TimeSpan.Zero;
        while (_loginInProgress && waited < InFlightLoginWaitTimeout)
        {
            await Task.Delay(InFlightLoginPollInterval);
            waited += InFlightLoginPollInterval;
        }

        AppendLog(_isLoggedIn
            ? $"[{source}] the in-flight login completed after {waited.TotalSeconds:F0}s; restoring previous run state."
            : $"[{source}] the in-flight login did not complete within {waited.TotalSeconds:F0}s.");
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

        var hasLiveBrowserSession = AccountSwitchPolicy.HasLiveBrowserSession(
            _isLoggedIn,
            _browserSessionLikelyOpen);

        // Only an authenticated, open browser session needs confirmation. With no live session the
        // dropdown is just choosing which saved account the next Login will use.
        if (hasLiveBrowserSession && !ConfirmAccountSwitch(FindAccount(current), selected))
        {
            AppendLog($"Account switch to '{selected.Name}' cancelled by user.");
            RefreshAccountPicker();
            return;
        }

        _accountSwitchInProgress = true;
        try
        {
            // Capture the previous account's options before switching so we can log it out cleanly.
            var previousOptions = ApplySelectedVillageToOptions(LoadBotOptions());
            var previousLoggedIn = hasLiveBrowserSession;

            await ResetForAccountSwitchAsync(previousOptions, previousLoggedIn);

            _accountStore.SetActive(selected.Name);
            RefreshAfterActiveAccountChanged(selected.Name);

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

    private bool ConfirmAccountSwitch(AccountEntry? currentAccount, AccountEntry newAccount)
    {
        var content = new AccountSwitchConfirmationView(currentAccount, newAccount);
        var answer = AppDialog.ShowCustomContent(
            this,
            content,
            "Switch account",
            [("Switch account", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.No)],
            MessageBoxImage.Warning,
            defaultResult: MessageBoxResult.No,
            cancelResult: MessageBoxResult.No,
            successResult: MessageBoxResult.Yes,
            width: 610);

        return answer == MessageBoxResult.Yes;
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

    private void RefreshAfterActiveAccountChanged(string accountName)
    {
        RefreshBrowserStatisticsUi();
        RecoverAndRefreshActiveAccountQueue();
        AppendLog($"Active account changed to '{accountName}'. Previous session closed and state reset.");
        ResetVillageSelectionUi();
        SyncServerFromActiveAccount();
        LoadConfigToUi();
        ConfigureSessionPacerFromConfig(reloadRuntime: true);
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
        _productionBackfillStateByVillage.Clear();
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
        _villageStatusCache.Clear();
        _buildingRows.Clear();
        _buildingCatalogOptions.Clear();
        _demolishableBuildings.Clear();
        BuildingsInfoTextBlock.Text = "Buildings not loaded yet.";
        VillagesInfoTextBlock.Text = "Villages: 0";
        // Gold/silver are account-scoped and deliberately sticky against unknown reads, so they must be
        // force-cleared here or the previous account's balance would stay on screen for the next one.
        SetGoldSilverStatusText(ServerResourcesTextBlock, SilverInfoTextBlock, "-", "-", allowClearToUnknown: true);
        ForceClearVillageSelectionUi();
        BuildQueueStatusTextBlock.Text = "Build queue: idle";

        _heroViewModel.ResetRuntimeState();
        _heroHomeVillageName = null;
        _heroHomeVillageKey = null;
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

    // Quick re-login: when enabled (Settings > General) and the last FULL post-login stack for this
    // account finished under the configured quick window, login only confirms the session — the snapshot/analyze
    // stack is skipped since nothing meaningful changes server-side that fast. The timestamp is
    // account-scoped, so switching account always runs the full stack.
    private const int QuickReloginWindowMinutes = 120;

    private bool IsQuickReloginWindowActive(out double minutesSinceFullLogin)
    {
        minutesSinceFullLogin = 0;
        try
        {
            var config = _botConfigStore.Load();
            // Default ON when the setting has never been saved.
            if (!(config[BotOptionPayloadKeys.PostLoginQuickReloginEnabled]?.GetValue<bool>() ?? true))
            {
                return false;
            }

            var raw = config[BotOptionPayloadKeys.PostLoginLastFullLoginAt]?.GetValue<string>();
            if (!DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastFullLogin))
            {
                return false;
            }

            minutesSinceFullLogin = (DateTimeOffset.UtcNow - lastFullLogin).TotalMinutes;
            return minutesSinceFullLogin >= 0 && minutesSinceFullLogin < QuickReloginWindowMinutes;
        }
        catch (Exception ex)
        {
            AppendLog($"[login] quick re-login check failed ({ex.Message}); running the full post-login stack.");
            return false;
        }
    }

    private void PersistLastFullPostLoginTimestamp()
    {
        try
        {
            var config = _botConfigStore.Load();
            config[BotOptionPayloadKeys.PostLoginLastFullLoginAt] = DateTimeOffset.UtcNow.ToString("O");
            _botConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not persist the full-login timestamp: {ex.Message}");
        }
    }

    private void RecoverAndRefreshActiveAccountQueue()
    {
        var recovered = _botService.ResetOrphanedRunningQueueItems();
        if (recovered > 0)
        {
            AppendLog($"Recovered {recovered} queue item(s) for '{_accountStore.ActiveAccountName()}' from Running to Pending (first retry in ~2 minutes).");
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
        // _browserSessionLikelyOpen are still true, the ~20s resource-refresh tick (and inbox checks)
        // can slip onto the session gate right after logout and silently log the OLD account back in.
        // Flipping these first makes ShouldRunBackgroundResourceSnapshotRefresh() bail; the wait below
        // then drains anything already in flight.
        _isLoggedIn = false;
        _browserSessionLikelyOpen = false;
        _inboxAutoEnabled = false;
        NotifySessionPacingOnlineStopped();
        await StopAllAutomationAndWaitAsync();

        // bot.json is global, so the previous account's village/farm-list pointers would otherwise leak
        // into the next account and make its first login navigate to a non-existent village. Strip them
        // FIRST — a crash/kill during the logout/shutdown below must not leave them behind for the next
        // start. The old account's logout uses previousOptions (already captured), so this is safe here.
        ClearPersistedAccountScopedConfig();

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
            BotOptionPayloadKeys.ReinforcementsSendMinMinutes,
            BotOptionPayloadKeys.ReinforcementsSendMaxMinutes,
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
