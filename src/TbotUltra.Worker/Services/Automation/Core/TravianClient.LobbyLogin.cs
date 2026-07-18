using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed record LobbyWorldCard(string WorldUid, string Name);
    private string? _pendingLobbyWorldUid;

    private async Task<bool> TryLoginThroughLobbyAsync(CancellationToken cancellationToken)
    {
        try
        {
            Notify($"[lobby-login] opening lobby for account '{_account.Name}'.");
            await GotoAsync(Paths.LobbyAccount, cancellationToken);
            ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());

            if (await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() == 0)
            {
                if (!await HasAnySelectorAsync(Selectors.LoginUsernameField)
                    || !await HasAnySelectorAsync(Selectors.LoginPasswordField))
                {
                    Notify("[lobby-login] neither an authenticated world list nor a login form was found.");
                    return false;
                }

                Notify("[lobby-login] lobby session is not authenticated; submitting credentials.");
                await FillLoginCredentialsWithPacingAsync(cancellationToken);
                await ClickLoginButtonAsync(cancellationToken);
                Notify("[lobby-login] credentials submitted; waiting for the loaded lobby world list.");
                if (!await WaitForLobbyWorldCardsAsync(cancellationToken))
                {
                    ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
                    Notify("[lobby-login] no world cards appeared after lobby credential submission.");
                    return false;
                }
            }

            var cards = await ReadLobbyWorldCardsAsync(cancellationToken);
            if (cards.Count == 0)
            {
                Notify("[lobby-login] authenticated lobby contained no usable owned worlds.");
                return false;
            }

            var analysisStore = new AccountAnalysisStore(_projectRoot);
            analysisStore.TryLoad(_account.Name, out var analysis, ServerUrl);
            var cachedWorldUid = Guid.TryParse(analysis?.WorldUid, out _)
                ? analysis!.WorldUid
                : null;

            IReadOnlyList<LobbyWorldCard> candidates = [];
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

            foreach (var candidate in candidates)
            {
                if (await TryEnterLobbyWorldAsync(candidate, cancellationToken))
                {
                    return true;
                }
                if (cachedWorldUid is not null)
                {
                    break;
                }
            }

            var selected = await RequestLobbyWorldSelectionAsync(cards, cancellationToken);
            if (selected is not null)
            {
                return await TryEnterLobbyWorldAsync(selected, cancellationToken);
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

    private async Task<LobbyWorldCard?> RequestLobbyWorldSelectionAsync(
        IReadOnlyList<LobbyWorldCard> cards,
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
                cards.Select(card => new LobbyWorldOption(card.WorldUid, card.Name)).ToList()),
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

    private async Task<bool> TryEnterLobbyWorldAsync(
        LobbyWorldCard candidate,
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
        var card = _page.Locator($"{Selectors.LobbyGameWorldCard}[data-wuid='{candidate.WorldUid}']").First;
        var playNow = card.Locator(Selectors.LobbyPlayNowButton).First;
        if (await playNow.CountAsync() == 0 || !await playNow.IsVisibleAsync() || !await playNow.IsEnabledAsync())
        {
            Notify($"[lobby-login] Play now is not actionable for '{candidate.Name}'.");
            return false;
        }

        await DelayBeforeClickAsync(cancellationToken, "lobby Play now");
        await SuppressConsentUiDuringSsoLandingAsync();
        await ClickPlayNowAndWaitForGameOriginAsync(playNow, cancellationToken);

        if (!IsConfiguredGameOrigin(_page.Url))
        {
            Notify($"[lobby-login] world '{candidate.Name}' landed on an unexpected or unauthenticated host '{SanitizeHost(_page.Url)}'.");
            return false;
        }

        _pendingLobbyWorldUid = candidate.WorldUid;
        Notify($"[lobby-login] configured game origin committed for world UID '{candidate.WorldUid}'; isolating before game scripts settle.");
        return true;
    }

    private async Task<AccountAccessState> ReadExplicitLobbyAccessStateAsync()
    {
        var state = await ProbeExplicitAccountAccessStateAsync(_page.Url.ToLowerInvariant());
        return state ?? AccountAccessState.LoggedOut;
    }

    private async Task<bool> WaitForLobbyWorldCardsAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _config.ManualLoginTimeoutSeconds));
        var navigationRetryLogged = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() > 0)
                {
                    await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                    {
                        Timeout = Math.Min(_config.TimeoutMs, 5000),
                    });
                    Notify("[lobby-login] lobby world list loaded and ready.");
                    return true;
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
            result.Add(new LobbyWorldCard(worldUid, name));
        }

        return result;
    }

    private async Task ClickPlayNowAndWaitForGameOriginAsync(
        ILocator playNow,
        CancellationToken cancellationToken)
    {
        var gameOriginCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameNavigated(object? _, IFrame frame)
        {
            if (ReferenceEquals(frame, _page.MainFrame) && IsConfiguredGameOrigin(frame.Url))
            {
                gameOriginCommitted.TrySetResult();
            }
        }

        _page.FrameNavigated += OnFrameNavigated;
        try
        {
            await playNow.ClickAsync(new LocatorClickOptions
            {
                Timeout = _config.TimeoutMs,
            });
            if (IsConfiguredGameOrigin(_page.Url))
            {
                return;
            }

            try
            {
                await gameOriginCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                // The caller reports the final host as an SSO failure.
            }
        }
        finally
        {
            _page.FrameNavigated -= OnFrameNavigated;
        }
    }

    private async Task SuppressConsentUiDuringSsoLandingAsync()
    {
        await _page.AddInitScriptAsync(
            """
            (() => {
              const install = () => {
                const root = document.documentElement;
                if (!root || document.getElementById('__tbot_sso_consent_suppression')) return;
                const style = document.createElement('style');
                style.id = '__tbot_sso_consent_suppression';
                style.textContent = `
                  #cmpbox, .cmpbox, [class*="cmpbox" i],
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
            """);
    }

    private bool IsConfiguredGameOrigin(string? url)
        => IsConfiguredGameOrigin(url, ServerUrl);

    internal static bool IsConfiguredGameOrigin(string? url, string serverUrl)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var landed)
            && Uri.TryCreate(serverUrl, UriKind.Absolute, out var configured)
            && landed.Scheme.Equals(configured.Scheme, StringComparison.OrdinalIgnoreCase)
            && landed.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase)
            && landed.Port == configured.Port;
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
