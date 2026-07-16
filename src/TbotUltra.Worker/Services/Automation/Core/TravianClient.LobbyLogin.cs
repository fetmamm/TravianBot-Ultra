using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private sealed record LobbyWorldCard(string WorldUid, string Name);

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

            IReadOnlyList<LobbyWorldCard> candidates;
            if (cachedWorldUid is not null)
            {
                candidates = cards
                    .Where(card => card.WorldUid.Equals(cachedWorldUid, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count == 0)
                {
                    Notify($"[lobby-login] cached world UID '{cachedWorldUid}' is not present in the lobby; using direct fallback.");
                    return false;
                }
            }
            else
            {
                candidates = cards
                    .Where(card => IsLobbyWorldNameMatch(card.Name, new Uri(ServerUrl).Host, _config.ServerName))
                    .ToList();
                if (candidates.Count == 0)
                {
                    Notify($"[lobby-login] no lobby world name matched server '{_config.ServerName}' ({ServerUrl}).");
                    return false;
                }
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsLobbyAccountUrl(_page.Url))
                {
                    await GotoAsync(Paths.LobbyAccount, cancellationToken);
                    if (!await WaitForLobbyWorldCardsAsync(cancellationToken))
                    {
                        Notify("[lobby-login] lobby world list did not return after checking a candidate.");
                        return false;
                    }
                }

                Notify($"[lobby-login] trying world '{candidate.Name}' wuid='{candidate.WorldUid}'.");
                var card = _page.Locator($"{Selectors.LobbyGameWorldCard}[data-wuid='{candidate.WorldUid}']").First;
                var playNow = card.Locator(Selectors.LobbyPlayNowButton).First;
                if (await playNow.CountAsync() == 0 || !await playNow.IsVisibleAsync() || !await playNow.IsEnabledAsync())
                {
                    Notify($"[lobby-login] Play now is not actionable for '{candidate.Name}'.");
                    continue;
                }

                await DelayBeforeClickAsync(cancellationToken, "lobby Play now");
                await playNow.ClickAsync(new LocatorClickOptions { Timeout = _config.TimeoutMs });
                await WaitForLobbySsoNavigationAsync(cancellationToken);

                if (IsConfiguredGameHost(_page.Url) && await WaitUntilLoggedInAsync(cancellationToken))
                {
                    analysisStore.SaveWorldUid(_account.Name, ServerUrl, candidate.WorldUid);
                    Notify($"[lobby-login] verified configured host and saved world UID '{candidate.WorldUid}'.");
                    return true;
                }

                Notify($"[lobby-login] world '{candidate.Name}' landed on an unexpected or unauthenticated host '{SanitizeHost(_page.Url)}'.");
                if (cachedWorldUid is not null)
                {
                    break;
                }
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

    private async Task<AccountAccessState> ReadExplicitLobbyAccessStateAsync()
    {
        var state = await ProbeExplicitAccountAccessStateAsync(_page.Url.ToLowerInvariant());
        return state ?? AccountAccessState.LoggedOut;
    }

    private async Task<bool> WaitForLobbyWorldCardsAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _config.ManualLoginTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAccountAccessBlocked(await ReadExplicitLobbyAccessStateAsync());
            if (await _page.Locator(Selectors.LobbyGameWorldCard).CountAsync() > 0)
            {
                return true;
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

    private async Task WaitForLobbySsoNavigationAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        DateTime? leftLobbyAt = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsConfiguredGameHost(_page.Url))
            {
                return;
            }

            if (!IsLobbyAccountUrl(_page.Url))
            {
                leftLobbyAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - leftLobbyAt >= TimeSpan.FromSeconds(5))
                {
                    return;
                }
            }

            await Task.Delay(Random.Shared.Next(150, 350), cancellationToken);
        }
    }

    private bool IsConfiguredGameHost(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var landed)
            && Uri.TryCreate(ServerUrl, UriKind.Absolute, out var configured)
            && landed.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase)
            && landed.AbsolutePath.Equals(Paths.Resources, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "invalid-url";

    internal static bool IsLobbyWorldNameMatch(string worldName, string serverHost, string? serverName)
    {
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

    private static HashSet<string> NormalizeLobbyWorldTokens(string value)
    {
        return Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{Nd}]+")
            .Select(match => match.Value)
            .Where(token => token is not ("server" or "world" or "gameworld" or "travian"))
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
