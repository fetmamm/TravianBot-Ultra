using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    internal const string MobileOptimizationsDialogSelector = "#mobileOptimizationsDialog";
    internal const string MobileOptimizationsSwitchSelector =
        MobileOptimizationsDialogSelector + " label.switch:has(input[name='mobileOptimizations'])";
    internal const string MobileOptimizationsPlayNowButtonSelector =
        MobileOptimizationsDialogSelector + " .action button.framed.green.withText";
    internal const string UnexpectedErrorPageSelector = "main#errorPage.unexpectedError";
    internal const string UnexpectedErrorBringMeBackButtonSelector =
        UnexpectedErrorPageSelector + " button.gold.decorative.buttonSecondary.withText";
    internal const string StartPageLoginButtonSelector =
        "header .headerContainerEnd button.gold.buttonSecondary.login.withText";
    internal const string StartPageGoToLobbyButtonSelector = "button.playNowCTAButton";
    private const int LobbyLoadAttempts = 3;

    private sealed record LobbyWorldCard(string WorldUid, string Name, string Details);
    private enum LobbyEntryState
    {
        Missing,
        WorldList,
        LoginForm,
    }

    private string? _pendingLobbyWorldUid;
    private LobbyWorldServerResolution? _pendingLobbyWorldServerResolution;

    private async Task<bool> TryLoginThroughLobbyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await PrepareAuthenticatedLobbyAsync(cancellationToken))
            {
                return false;
            }

            var cards = await ReadLobbyWorldCardsAsync(cancellationToken);
            if (cards.Count == 0)
            {
                Notify("[lobby-login] authenticated lobby contained no usable game worlds.");
                return false;
            }

            IReadOnlyList<LobbyWorldCard> candidates = [];
            string? cachedWorldUid = null;
            if (ShouldAlwaysRequestLobbyWorldSelection(_config.ServerName))
            {
                Notify("[lobby-login] account is set to 'Choose in lobby'; requesting a world selection.");
            }
            else
            {
                var analysisStore = new AccountAnalysisStore(_projectRoot);
                analysisStore.TryLoad(_account.Name, out var analysis, ServerUrl);
                cachedWorldUid = Guid.TryParse(analysis?.WorldUid, out _)
                    ? analysis!.WorldUid
                    : null;

                if (cachedWorldUid is not null)
                {
                    candidates = cards
                        .Where(card => card.WorldUid.Equals(cachedWorldUid, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        Notify($"[lobby-login] cached world UID '{cachedWorldUid}' is not present in the lobby.");
                        cachedWorldUid = null;
                    }
                }

                if (cachedWorldUid is null)
                {
                    candidates = cards
                        .Where(card => IsLobbyWorldNameMatch(card.Name, new Uri(ServerUrl).Host, _config.ServerName))
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        Notify($"[lobby-login] no lobby world name matched server '{_config.ServerName}' ({ServerUrl}).");
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                if (await TryEnterLobbyWorldAsync(candidate, allowServerCorrection: false, cancellationToken))
                {
                    return true;
                }
                if (cachedWorldUid is not null)
                {
                    break;
                }
            }

            if (!ShouldRequestLobbyWorldSelectionAfterAutomaticAttempts(_config.ServerName))
            {
                Notify("[lobby-login] the saved world did not reach its configured origin; keeping the account server unchanged and leaving retry handling to the caller.");
                return false;
            }

            var manuallyFailedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (manuallyFailedWorlds.Count < cards.Count)
            {
                var selectableCards = cards
                    .Where(card => !manuallyFailedWorlds.Contains(card.WorldUid))
                    .ToList();
                var selected = await RequestLobbyWorldSelectionAsync(
                    selectableCards,
                    manuallyFailedWorlds.Count > 0,
                    cancellationToken);
                if (selected is null)
                {
                    return false;
                }

                if (await TryEnterLobbyWorldAsync(selected, allowServerCorrection: true, cancellationToken))
                {
                    return true;
                }

                manuallyFailedWorlds.Add(selected.WorldUid);
                Notify($"[lobby-login] selected world '{selected.Name}' did not reach the configured origin; reopening the picker with the remaining worlds.");
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountAccessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException or UriFormatException)
        {
            Notify($"[lobby-login] flow failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PrepareAuthenticatedLobbyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= LobbyLoadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Notify($"[lobby-login] opening lobby for account '{_account.Name}' (attempt {attempt}/{LobbyLoadAttempts}).");
                await GotoAsync(Paths.LobbyAccount, cancellationToken);
                if (await TryRecoverFromUnexpectedLobbyErrorPageAsync(cancellationToken))
                {
                    await TryResumeLobbyFromRecoveredStartPageAsync(cancellationToken);
                }

                ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
                var entryState = await WaitForLobbyEntryStateAsync(cancellationToken);
                if (entryState == LobbyEntryState.WorldList)
                {
                    return true;
                }

                if (entryState == LobbyEntryState.LoginForm)
                {
                    if (_account.ManualLogin)
                    {
                        if (_manualLoginConfirmationRequested is null)
                        {
                            throw new InvalidOperationException("Manual login requires an interactive confirmation host.");
                        }

                        Notify("[manual-login] lobby is ready; waiting for the user to complete login in the browser.");
                        string? validationMessage = null;
                        while (true)
                        {
                            var loginDone = await _manualLoginConfirmationRequested(
                                new ManualLoginConfirmationRequest(validationMessage),
                                cancellationToken);
                            if (!loginDone)
                            {
                                throw new ManualLoginCanceledException();
                            }

                            Notify("[manual-login] Login done clicked; verifying the authenticated lobby.");
                            if (await WaitForLobbyWorldCardsAsync(
                                    cancellationToken,
                                    timeoutSeconds: 5,
                                    allowCredentialRecovery: false))
                            {
                                Notify("[manual-login] authenticated lobby verified.");
                                return true;
                            }

                            validationMessage = "Login was not confirmed in the Travian lobby. Finish signing in, then try again.";
                            Notify("[manual-login] lobby authentication was not confirmed; continuing to wait for the user.");
                        }
                    }

                    Notify($"[lobby-login] lobby session is not authenticated; submitting credentials (attempt {attempt}/{LobbyLoadAttempts}).");
                    await FillLoginCredentialsWithPacingAsync(cancellationToken);
                    await ClickLoginButtonAsync(cancellationToken);
                    Notify("[lobby-login] credentials submitted; waiting for the loaded lobby world list.");
                    if (await WaitForLobbyWorldCardsAsync(cancellationToken))
                    {
                        return true;
                    }

                    ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
                    Notify($"[lobby-login] no world cards appeared after credential submission (attempt {attempt}/{LobbyLoadAttempts}).");
                }
                else
                {
                    ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
                    Notify($"[lobby-login] neither an authenticated world list nor a login form rendered (attempt {attempt}/{LobbyLoadAttempts}).");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AccountAccessException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                Notify($"[lobby-login] transient lobby attempt {attempt}/{LobbyLoadAttempts} failed: {ex.Message}");
            }

            if (attempt < LobbyLoadAttempts)
            {
                Notify($"[lobby-login] retrying the lobby load ({attempt + 1}/{LobbyLoadAttempts}).");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        Notify($"[lobby-login] lobby authentication did not complete after {LobbyLoadAttempts} attempts.");
        return false;
    }

    private async Task<LobbyEntryState> WaitForLobbyEntryStateAsync(CancellationToken cancellationToken)
    {
        var renderTimeoutMs = Math.Clamp(_config.TimeoutMs, 10_000, 30_000);
        var deadline = DateTime.UtcNow.AddMilliseconds(renderTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() > 0)
                {
                    return LobbyEntryState.WorldList;
                }

                if (await HasAnySelectorAsync(Selectors.LoginUsernameField)
                    && await HasAnySelectorAsync(Selectors.LoginPasswordField))
                {
                    return LobbyEntryState.LoginForm;
                }
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                // The lobby may replace its initial document while React is starting; keep polling.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return LobbyEntryState.Missing;
    }

    private async Task<LobbyWorldCard?> RequestLobbyWorldSelectionAsync(
        IReadOnlyList<LobbyWorldCard> cards,
        bool previousSelectionFailed,
        CancellationToken cancellationToken)
    {
        if (!_interactive || _lobbyWorldSelectionRequested is null)
        {
            return null;
        }

        Notify($"[lobby-login] requesting manual selection from {cards.Count} owned lobby world(s).");
        var selectedWorldUid = await _lobbyWorldSelectionRequested(
            new LobbyWorldSelectionRequest(
                _config.ServerName,
                ServerUrl,
                cards.Select(card => new LobbyWorldOption(card.WorldUid, card.Name, card.Details)).ToList(),
                previousSelectionFailed),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(selectedWorldUid))
        {
            Notify("[lobby-login] manual world selection was cancelled.");
            return null;
        }

        var selected = cards.FirstOrDefault(card =>
            card.WorldUid.Equals(selectedWorldUid, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            Notify("[lobby-login] manual world selection returned a world that is no longer present.");
        }
        return selected;
    }

    internal static bool ShouldAlwaysRequestLobbyWorldSelection(string? serverName)
        => ShouldRequestLobbyWorldSelectionAfterAutomaticAttempts(serverName);

    internal static bool ShouldRequestLobbyWorldSelectionAfterAutomaticAttempts(string? serverName)
        => LobbyWorldSelectionDefaults.IsChooseInLobby(serverName);

    internal static bool ShouldRetryPlayNow(
        int completedAttempts,
        bool remainsOnLobby,
        bool worldCardActionable)
        => completedAttempts < 2 && remainsOnLobby && worldCardActionable;

    private async Task<bool> TryEnterLobbyWorldAsync(
        LobbyWorldCard candidate,
        bool allowServerCorrection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLobbyAccountUrl(_page.Url))
        {
            await GotoAsync(Paths.LobbyAccount, cancellationToken);
            if (!await WaitForLobbyWorldCardsAsync(cancellationToken))
            {
                Notify("[lobby-login] lobby world list did not return before selecting a world.");
                return false;
            }
        }

        Notify($"[lobby-login] trying world '{candidate.Name}' wuid='{candidate.WorldUid}'.");
        for (var playAttempt = 1; playAttempt <= 2; playAttempt++)
        {
            await TryHandleMobileOptimizationsDialogAsync(cancellationToken);
            var card = _page.Locator($"{Selectors.LobbyGameWorldCard}[data-wuid='{candidate.WorldUid}']").First;
            var playNow = card.Locator(Selectors.LobbyPlayNowButton).First;
            var playNowActionable = await playNow.CountAsync() > 0
                && await playNow.IsVisibleAsync()
                && await playNow.IsEnabledAsync();
            if (!playNowActionable)
            {
                Notify($"[lobby-login] Play now is not actionable for '{candidate.Name}'.");
                return false;
            }

            await DelayBeforeClickAsync(cancellationToken, $"lobby Play now (attempt {playAttempt}/2)");
            await SuppressConsentUiDuringSsoLandingAsync();
            var gameOriginCommitted = await ClickPlayNowAndWaitForGameOriginAsync(
                playNow,
                allowServerCorrection,
                cancellationToken);
            if (!gameOriginCommitted
                && await WaitForMobileOptimizationsDialogAfterWorldSelectionAsync(cancellationToken))
            {
                await WaitForGameOriginAfterMobileConfirmationAsync(allowServerCorrection, cancellationToken);
                gameOriginCommitted = IsConfiguredGameOrigin(_page.Url)
                    || (allowServerCorrection && TryResolveOfficialGameOrigin(_page.Url, out _));
            }

            if (gameOriginCommitted)
            {
                break;
            }

            var remainsOnLobby = IsLobbyAccountUrl(_page.Url);
            var freshPlayNow = _page
                .Locator($"{Selectors.LobbyGameWorldCard}[data-wuid='{candidate.WorldUid}']")
                .First
                .Locator(Selectors.LobbyPlayNowButton)
                .First;
            var freshPlayNowActionable = remainsOnLobby
                && await freshPlayNow.CountAsync() > 0
                && await freshPlayNow.IsVisibleAsync()
                && await freshPlayNow.IsEnabledAsync();
            if (!ShouldRetryPlayNow(playAttempt, remainsOnLobby, freshPlayNowActionable))
            {
                break;
            }

            Notify($"[lobby-login] Play now attempt {playAttempt}/2 did not leave the lobby after a network failure; retrying the fresh world card once.");
        }

        var configuredOriginReached = IsConfiguredGameOrigin(_page.Url);
        if (!configuredOriginReached
            && (!allowServerCorrection || !TryResolveOfficialGameOrigin(_page.Url, out _)))
        {
            Notify($"[lobby-login] world '{candidate.Name}' landed on an unexpected or unauthenticated host '{SanitizeHost(_page.Url)}'.");
            return false;
        }

        // A manual choice is authoritative even when its URL happens to match the configured origin:
        // the lobby world name may still correct stale or mistyped Manage data.
        if (allowServerCorrection && TryResolveOfficialGameOrigin(_page.Url, out var resolvedServerUrl))
        {
            _resolvedServerUrl = resolvedServerUrl;
            _pendingLobbyWorldServerResolution = new LobbyWorldServerResolution(
                _account.Name,
                candidate.WorldUid,
                candidate.Name,
                resolvedServerUrl);
            Notify($"[lobby-login] manually selected game world resolved to '{SanitizeHost(resolvedServerUrl)}'; account correction will be saved after game login.");
        }

        _pendingLobbyWorldUid = candidate.WorldUid;
        Notify($"[lobby-login] configured game origin committed for world UID '{candidate.WorldUid}'; isolating before game scripts settle.");
        return true;
    }

    private async Task<bool> TryHandleMobileOptimizationsDialogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dialog = _page.Locator(MobileOptimizationsDialogSelector).First;
            if (await dialog.CountAsync() == 0 || !await dialog.IsVisibleAsync())
            {
                return false;
            }

            Notify("[lobby-login] mobile version dialog detected; disabling mobile options.");
            var switches = dialog.Locator("label.switch:has(input[name='mobileOptimizations'])");
            var switchCount = await switches.CountAsync();
            if (switchCount != 2)
            {
                throw new InvalidOperationException($"Mobile version dialog contained {switchCount} mobile option switches instead of two.");
            }

            for (var switchIndex = 0; switchIndex < switchCount; switchIndex++)
            {
                await DisableMobileOptimizationSwitchAsync(switches.Nth(switchIndex), cancellationToken);
            }

            var useMobileBrowserVersion = switches.Nth(0).Locator("input[name='mobileOptimizations']").First;
            var askAboutMobileBrowserVersion = switches.Nth(1).Locator("input[name='mobileOptimizations']").First;
            if (await useMobileBrowserVersion.IsCheckedAsync())
            {
                throw new InvalidOperationException("Mobile version dialog still has 'Use the mobile browser version' enabled.");
            }

            if (await askAboutMobileBrowserVersion.IsCheckedAsync())
            {
                throw new InvalidOperationException("Mobile version dialog still has 'Ask me every time' enabled.");
            }

            if (!await TryClickFirstVisibleEnabledAsync(
                    MobileOptimizationsPlayNowButtonSelector,
                    cancellationToken,
                    requiredText: "Play now",
                    requireExactText: true,
                    reason: "confirm mobile version dialog"))
            {
                throw new InvalidOperationException("Mobile version dialog Play now button was unavailable.");
            }

            Notify("[lobby-login] mobile version dialog confirmed with both mobile options disabled.");
            return true;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task DisableMobileOptimizationSwitchAsync(ILocator mobileSwitch, CancellationToken cancellationToken)
    {
        var input = mobileSwitch.Locator("input[name='mobileOptimizations']").First;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await input.IsCheckedAsync())
            {
                return;
            }

            try
            {
                await DelayBeforeClickAsync(cancellationToken, "disable mobile version option");
                await mobileSwitch.ClickAsync(new LocatorClickOptions
                {
                    Timeout = _config.TimeoutMs,
                });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                Notify($"[lobby-login] mobile version switch click failed on attempt {attempt + 1}; using native input fallback: {ex.Message}");
            }

            if (!await input.IsCheckedAsync())
            {
                return;
            }

            Notify($"[lobby-login] mobile version switch remained enabled on attempt {attempt + 1}; applying native input fallback.");
            await input.EvaluateAsync<bool>(
                """
                node => {
                  if (!(node instanceof HTMLInputElement) || !node.checked) return true;
                  const checkedSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'checked')?.set;
                  if (!checkedSetter) return false;
                  checkedSetter.call(node, false);
                  node.dispatchEvent(new Event('input', { bubbles: true }));
                  node.dispatchEvent(new Event('change', { bubbles: true }));
                  return !node.checked;
                }
                """);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);

            if (!await input.IsCheckedAsync())
            {
                return;
            }
        }

        throw new InvalidOperationException("Mobile version dialog showed an enabled option that could not be turned off.");
    }

    private async Task<bool> WaitForMobileOptimizationsDialogAfterWorldSelectionAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryHandleMobileOptimizationsDialogAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private async Task WaitForGameOriginAfterMobileConfirmationAsync(
        bool allowServerCorrection,
        CancellationToken cancellationToken)
    {
        Notify("[lobby-login] mobile version dialog closed; waiting for the game navigation to commit.");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsConfiguredGameOrigin(_page.Url)
                || (allowServerCorrection && TryResolveOfficialGameOrigin(_page.Url, out _)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private async Task<AccountAccessState> ReadExplicitLobbyAccessStateAsync()
    {
        var state = await ProbeExplicitAccountAccessStateAsync(_page.Url.ToLowerInvariant());
        return state ?? AccountAccessState.LoggedOut;
    }

    private async Task<bool> WaitForLobbyWorldCardsAsync(
        CancellationToken cancellationToken,
        int? timeoutSeconds = null,
        bool allowCredentialRecovery = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds ?? Math.Max(10, _config.ManualLoginTimeoutSeconds));
        var navigationRetryLogged = false;
        var unexpectedErrorRecoveryAttempted = false;
        var inlineErrorRecoveryAttempts = 0;
        const int maxInlineErrorRecoveries = 2;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!unexpectedErrorRecoveryAttempted
                    && await TryRecoverFromUnexpectedLobbyErrorPageAsync(cancellationToken))
                {
                    unexpectedErrorRecoveryAttempted = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                    continue;
                }

                if (await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() > 0)
                {
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                    {
                        Timeout = Math.Min(_config.TimeoutMs, 5000),
                    });
                    Notify("[lobby-login] lobby world list loaded and ready.");
                    return true;
                }

                // The lobby form can reject the first submit with an inline "Unexpected error"
                // (data-rule="customErrorExists") and keep the Login button disabled until a field is
                // edited. Re-entering the credentials clears that custom error and re-enables submit, so
                // resubmit a bounded number of times before giving up.
                if (allowCredentialRecovery
                    && inlineErrorRecoveryAttempts < maxInlineErrorRecoveries
                    && await IsLobbyLoginUnexpectedErrorBlockingSubmitAsync())
                {
                    inlineErrorRecoveryAttempts++;
                    Notify($"[lobby-login] inline 'Unexpected error' disabled the Login button; re-entering credentials and resubmitting (attempt {inlineErrorRecoveryAttempts}/{maxInlineErrorRecoveries}).");
                    try
                    {
                        await FillLoginCredentialsWithPacingAsync(cancellationToken);
                        await ClickLoginButtonAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                    {
                        Notify($"[lobby-login] resubmit after inline error did not complete ({ex.Message}); continuing to wait.");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
                    continue;
                }

                ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
            }
            catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
            {
                if (!navigationRetryLogged)
                {
                    Notify("[lobby-login:verbose] lobby is navigating after credential submit; waiting for the new document.");
                    navigationRetryLogged = true;
                }
            }

            await Task.Delay(Random.Shared.Next(400, 600), cancellationToken);
        }

        return false;
    }

    // True when the lobby login modal shows the inline "Unexpected error" (customErrorExists) on the
    // password field and the Login submit button is disabled — the state the user has to clear manually
    // by re-focusing a field. Scoped to the #loginLobby modal so it never matches an authenticated lobby.
    private async Task<bool> IsLobbyLoginUnexpectedErrorBlockingSubmitAsync()
    {
        try
        {
            return await _page.EvaluateAsync<bool>(
                """
                () => {
                  const modal = document.querySelector('#loginLobby');
                  if (!modal) return false;
                  const submit = modal.querySelector("button[type='submit']");
                  const submitDisabled = !!submit
                    && (submit.disabled || submit.getAttribute('disabled') !== null);
                  const hasCustomError = Array.from(
                    modal.querySelectorAll('.validation[data-rule="customErrorExists"]'))
                    .some(node => node.classList.contains('show'));
                  return submitDisabled && hasCustomError;
                }
                """);
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<bool> TryRecoverFromUnexpectedLobbyErrorPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var errorPage = _page.Locator(UnexpectedErrorPageSelector).First;
            if (await errorPage.CountAsync() == 0 || !await errorPage.IsVisibleAsync())
            {
                return false;
            }

            Notify("[lobby-login] Travian unexpected error page detected; returning to the start page.");
            if (!await TryClickFirstVisibleEnabledAsync(
                    UnexpectedErrorBringMeBackButtonSelector,
                    cancellationToken,
                    requiredText: "Bring me back",
                    requireExactText: true,
                    reason: "return from Travian error page"))
            {
                Notify("[lobby-login] unexpected error page had no actionable Bring me back button.");
                return false;
            }

            Notify("[lobby-login] clicked Bring me back; resolving the start-page login route.");
            return true;
        }
        catch (PlaywrightException ex) when (IsTransientExecutionContextError(ex))
        {
            return false;
        }
    }

    private async Task<bool> TryResumeLobbyFromRecoveredStartPageAsync(CancellationToken cancellationToken)
    {
        var goToLobbyClickAttempted = false;
        var loginClickAttempted = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLobbyAccountUrl(_page.Url)
                || await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() > 0
                || await HasAnySelectorAsync(Selectors.LoginUsernameField))
            {
                return true;
            }

            if (!goToLobbyClickAttempted
                && await TryClickFirstVisibleEnabledAsync(
                    StartPageGoToLobbyButtonSelector,
                    cancellationToken,
                    requiredText: "Go to lobby",
                    requireExactText: true,
                    reason: "open lobby from Travian start page"))
            {
                goToLobbyClickAttempted = true;
                Notify("[lobby-login] clicked start-page Go to lobby after the unexpected error page.");
            }
            else if (!goToLobbyClickAttempted
                && !loginClickAttempted
                && await TryClickFirstVisibleEnabledAsync(
                    StartPageLoginButtonSelector,
                    cancellationToken,
                    requiredText: "Login",
                    requireExactText: true,
                    reason: "open login from Travian start page"))
            {
                loginClickAttempted = true;
                Notify("[lobby-login] clicked start-page Login after the unexpected error page.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        Notify("[lobby-login] start-page Go to lobby/Login was not available after the unexpected error page.");
        return false;
    }

    private async Task<IReadOnlyList<LobbyWorldCard>> ReadLobbyWorldCardsAsync(CancellationToken cancellationToken)
    {
        var result = new List<LobbyWorldCard>();
        var cards = _page.Locator(Selectors.LobbyGameWorldCard);
        var count = await cards.CountAsync();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var card = cards.Nth(index);
            var worldUid = await card.GetAttributeAsync("data-wuid") ?? string.Empty;
            if (!Guid.TryParse(worldUid, out _))
            {
                continue;
            }

            var nameLocator = card.Locator(Selectors.LobbyGameWorldName).First;
            var name = await nameLocator.CountAsync() > 0
                ? (await nameLocator.InnerTextAsync()).Trim()
                : string.Empty;
            var details = Regex.Replace(await card.InnerTextAsync(), @"\s+", " ").Trim();
            result.Add(new LobbyWorldCard(worldUid, name, details));
        }

        return result;
    }

    private async Task<bool> ClickPlayNowAndWaitForGameOriginAsync(
        ILocator playNow,
        bool allowServerCorrection,
        CancellationToken cancellationToken)
    {
        var gameOriginCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxyRequestFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameNavigated(object? sender, IFrame frame)
        {
            if (ReferenceEquals(frame, _page.MainFrame)
                && (IsConfiguredGameOrigin(frame.Url)
                    || (allowServerCorrection && TryResolveOfficialGameOrigin(frame.Url, out _))))
            {
                gameOriginCommitted.TrySetResult();
            }
        }

        void OnRequestFailed(object? sender, IRequest request)
        {
            var isLobbyRequest = Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri)
                && (requestUri.Host.Equals("lobby.legends.travian.com", StringComparison.OrdinalIgnoreCase)
                    || requestUri.Host.Equals("www.travian.com", StringComparison.OrdinalIgnoreCase));
            if (isLobbyRequest && ProxyParser.LooksLikeProxyError(request.Failure))
            {
                proxyRequestFailed.TrySetResult();
            }
        }

        _page.FrameNavigated += OnFrameNavigated;
        _page.RequestFailed += OnRequestFailed;
        try
        {
            await playNow.ClickAsync(new LocatorClickOptions
            {
                Timeout = _config.TimeoutMs,
            });
            if (IsConfiguredGameOrigin(_page.Url)
                || (allowServerCorrection && TryResolveOfficialGameOrigin(_page.Url, out _)))
            {
                return true;
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            var completed = await Task.WhenAny(gameOriginCommitted.Task, proxyRequestFailed.Task, timeout);
            if (completed == gameOriginCommitted.Task)
            {
                await gameOriginCommitted.Task;
                return true;
            }

            if (completed == proxyRequestFailed.Task)
            {
                Notify("[lobby-login] Play now request failed through the configured proxy before the game origin committed.");
            }

            await completed;
            return false;
        }
        finally
        {
            _page.FrameNavigated -= OnFrameNavigated;
            _page.RequestFailed -= OnRequestFailed;
        }
    }

    private async Task SuppressConsentUiDuringSsoLandingAsync()
    {
        const string suppressionScript =
            """
            (() => {
              const install = () => {
                const root = document.documentElement;
                if (!root || document.getElementById('__tbot_sso_consent_suppression')) return;
                const style = document.createElement('style');
                style.id = '__tbot_sso_consent_suppression';
                style.textContent = `
                  #cmpbox, #cmpwrapper, .cmpbox, [class*="cmpbox" i],
                  iframe[src*="consentmanager" i],
                  [id*="consent" i][role="dialog"],
                  [class*="consent" i][role="dialog"] {
                    display: none !important;
                    visibility: hidden !important;
                    opacity: 0 !important;
                  }
                `;
                root.prepend(style);
              };
              install();
              if (!document.documentElement) {
                new MutationObserver((_, observer) => {
                  if (document.documentElement) {
                    install();
                    observer.disconnect();
                  }
                }).observe(document, { childList: true, subtree: true });
              }
            })();
            """;
        await _page.AddInitScriptAsync(suppressionScript);
        await _page.EvaluateAsync(suppressionScript);
    }

    private bool IsConfiguredGameOrigin(string? url)
        => IsConfiguredGameOrigin(url, ServerUrl);

    internal static bool IsConfiguredGameOrigin(string? url, string serverUrl)
    {
        // "Choose in lobby" uses the lobby as the temporary configured base URL. The lobby must never
        // count as a reached game origin, otherwise its own navigation after Play now is accepted as
        // success before the selected world's redirect commits.
        return TryResolveOfficialGameOrigin(url, out _)
            && Uri.TryCreate(url, UriKind.Absolute, out var landed)
            && Uri.TryCreate(serverUrl, UriKind.Absolute, out var configured)
            && landed.Scheme.Equals(configured.Scheme, StringComparison.OrdinalIgnoreCase)
            && landed.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase)
            && landed.Port == configured.Port;
    }

    internal static bool TryResolveOfficialGameOrigin(string? url, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.EndsWith(".travian.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries).Length < 4
            || uri.Host.StartsWith("lobby.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static string SanitizeHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid-url";

    internal static bool IsLobbyWorldNameMatch(string worldName, string serverHost, string? serverName)
    {
        var worldSpeed = ReadLobbySpeed(worldName);
        var configuredSpeed = ReadLobbySpeed(serverName ?? string.Empty) ?? ReadLobbySpeed(serverHost);
        if (worldSpeed is not null
            && configuredSpeed is not null
            && !string.Equals(worldSpeed, configuredSpeed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var worldTokens = NormalizeLobbyWorldTokens(worldName);
        if (worldTokens.Count == 0)
        {
            return false;
        }

        var configuredNameTokens = NormalizeLobbyWorldTokens(serverName ?? string.Empty);
        if (configuredNameTokens.Count > 0 && configuredNameTokens.All(worldTokens.Contains))
        {
            return true;
        }

        var hostTokens = NormalizeLobbyHostTokens(serverHost);
        return hostTokens.Count > 0 && hostTokens.All(worldTokens.Contains);
    }

    private static string? ReadLobbySpeed(string value)
    {
        var match = Regex.Match(value, @"(?:^|[^\p{L}\p{Nd}])x(?<speed>\d+)(?:$|[^\p{L}\p{Nd}])", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["speed"].Value : null;
    }

    private static HashSet<string> NormalizeLobbyWorldTokens(string value)
    {
        return Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{Nd}]+")
            .Select(match => match.Value)
            .Where(token => token is not ("server" or "world" or "gameworld" or "travian")
                && !Regex.IsMatch(token, @"^x\d+$"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> NormalizeLobbyHostTokens(string host)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in host.ToLowerInvariant().Split('.'))
        {
            if (label is "travian" or "com" or "net" or "org" or "legends" || Regex.IsMatch(label, @"^x\d+$"))
            {
                continue;
            }

            var serverNumber = Regex.Match(label, @"^ts(?<number>\d+)$");
            if (serverNumber.Success)
            {
                tokens.Add(serverNumber.Groups["number"].Value);
                continue;
            }

            foreach (Match part in Regex.Matches(label, @"[\p{L}]+|[\p{Nd}]+"))
            {
                if (part.Value is not ("ts" or "server"))
                {
                    tokens.Add(part.Value);
                }
            }
        }

        return tokens;
    }
}
