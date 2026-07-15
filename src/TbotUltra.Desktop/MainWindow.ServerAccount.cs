using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

// Server- and account-related helpers: syncing the active account's server into
// bot config, fetching/merging server options, the account picker, and the
// account/server info labels. Extracted verbatim from MainWindow.xaml.cs to keep
// that file focused; same class, so this is a pure relocation with no behavior
// change.
public partial class MainWindow
{
    private void SyncServerFromActiveAccount()
    {
        try
        {
            var activeName = _accountStore.ActiveAccountName();
            var account = _accountStore
                .ListAccounts()
                .FirstOrDefault(item => string.Equals(item.Name, activeName, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                UpdateAccountInfoLabel(activeName);
                return;
            }

            UpdateAccountInfoLabel(account.Name);
            var targetUrl = account.ServerUrl?.Trim().TrimEnd('/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return;
            }

            var config = _botConfigStore.Load();
            var currentUrl = (config["base_url"]?.GetValue<string>() ?? string.Empty).TrimEnd('/');
            var currentName = config["server_name"]?.GetValue<string>() ?? string.Empty;
            var changed = false;

            if (!string.IsNullOrWhiteSpace(account.ServerName)
                && !string.Equals(currentName, account.ServerName, StringComparison.OrdinalIgnoreCase))
            {
                config["server_name"] = account.ServerName;
                changed = true;
            }

            if (!string.Equals(currentUrl, targetUrl, StringComparison.OrdinalIgnoreCase))
            {
                config["base_url"] = targetUrl;
                changed = true;
            }

            if (changed)
            {
                _botConfigStore.Save(config);
                AppendLog($"Server synced from active account '{account.Name}'.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not sync server from account: {ex.Message}");
        }
    }

    private Task<List<ServerOption>> FetchDefaultServerOptionsAsync(BotOptions options)
    {
        var servers = new List<ServerOption>
        {
            new ServerOption
            {
                Name = options.ServerName,
                BaseUrl = options.BaseUrl,
            },
        };

        return Task.FromResult(servers);
    }

    private List<ServerOption> FetchEffectiveServerOptions(IEnumerable<ServerOption> defaultServers)
    {
        try
        {
            var customServers = _serverCatalogStore.Load();
            if (customServers.Count > 0)
            {
                return customServers;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load user server list, using defaults: {ex.Message}");
        }

        return defaultServers
            .Select(item => new ServerOption
            {
                Name = item.Name,
                BaseUrl = item.BaseUrl,
            })
            .ToList();
    }

    private void RefreshAccountPicker()
    {
        try
        {
            var active = _accountStore.ActiveAccountName();
            var accounts = _accountStore.ListAccounts()
                .OrderByDescending(item => string.Equals(item.Name, active, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressAccountSelectionChange = true;
            try
            {
                AccountComboBox.ItemsSource = accounts;
                var selected = accounts.FirstOrDefault(item =>
                                   string.Equals(item.Name, active, StringComparison.OrdinalIgnoreCase))
                               ?? accounts.FirstOrDefault();
                AccountComboBox.SelectedItem = selected;
            }
            finally
            {
                _suppressAccountSelectionChange = false;
            }

            RefreshAccountHoldUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Could not refresh account list: {ex.Message}");
        }
    }

    private void UpdateAccountInfoLabel(string accountName)
    {
        // Show the server name the user entered for the active account (e.g. "X5 Asia"), not the username.
        var username = accountName;
        string? serverName = null;
        AccountEntry? account = null;
        try
        {
            account = _accountStore
                .ListAccounts()
                .FirstOrDefault(item => string.Equals(item.Name, accountName, StringComparison.OrdinalIgnoreCase));
            if (account is not null)
            {
                if (!string.IsNullOrWhiteSpace(account.Username))
                {
                    username = account.Username;
                }

                serverName = account.ServerName;
            }
        }
        catch
        {
            // Fall back to the configured server / account name below.
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = LoadBotOptions().ServerName;
        }

        // Fall back to the username only when no server name is known.
        ActiveAccountInfoTextBlock.Text = string.IsNullOrWhiteSpace(serverName)
            ? username
            : serverName.Trim();

        UpdateProxyStatus(account);
    }

    // Sidebar proxy indicator: grey/"No proxy is used" when off, green + country when on. The country
    // is looked up once through the proxy (and cached per server) so the user can tell at a glance that
    // the proxy is active, where it exits, and whether it actually responds.
    private const string DirectConnectionLookupKey = "direct";
    private string _proxyStatusServer = string.Empty;
    private string _proxyStatusLookupKey = string.Empty;
    private string _proxyStatusIp = string.Empty;
    private string _proxyStatusCountry = string.Empty;
    private CancellationTokenSource? _proxyStatusCts;

    private void UpdateProxyStatus(AccountEntry? account)
    {
        if (account is null)
        {
            _proxyStatusLookupKey = string.Empty;
            _proxyStatusServer = string.Empty;
            _proxyStatusIp = string.Empty;
            _proxyStatusCountry = string.Empty;
            _proxyStatusCts?.Cancel();
            SetProxyStatusVisual("TextMutedBrush", "No proxy is used", detail: null);
            return;
        }

        var enabled = account?.ProxyEnabled == true && !string.IsNullOrWhiteSpace(account.ProxyServer);
        var server = enabled ? account!.ProxyServer.Trim() : string.Empty;
        var lookupKey = enabled ? server : DirectConnectionLookupKey;

        if (enabled
            && string.Equals(lookupKey, _proxyStatusLookupKey, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_proxyStatusIp))
        {
            ApplyConnectionStatusVisual(enabled, server, _proxyStatusIp, _proxyStatusCountry);
            return;
        }

        _proxyStatusLookupKey = lookupKey;
        _proxyStatusServer = server;
        _proxyStatusIp = string.Empty;
        _proxyStatusCountry = string.Empty;
        if (enabled)
        {
            SetProxyStatusVisual("WarningTextBrush", "Checking…", $"Proxy: {ProxyHostPortForDisplay(server)}");
        }
        else
        {
            SetProxyStatusVisual("TextMutedBrush", "No proxy is used", "IP: Checking…");
        }

        _ = RefreshConnectionIdentityAsync(lookupKey, enabled ? server : null);
    }

    private void ApplyConnectionStatusVisual(bool isProxy, string server, string ip, string country)
    {
        if (!isProxy)
        {
            SetProxyStatusVisual("TextMutedBrush", "No proxy is used", $"IP: {ip}");
            return;
        }

        var label = ResolveProxyLabel(server, country);
        SetProxyStatusVisual("SuccessBrush", label, ProxyHostPortForDisplay(server));
    }

    private static string ResolveProxyLabel(string server, string country)
    {
        try
        {
            var entry = new ProxyLibraryStore().FindByServer(server);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Name))
            {
                return entry.Name.Trim();
            }
        }
        catch
        {
            // The live country lookup below is still sufficient if the proxy library cannot be read.
        }

        return string.IsNullOrWhiteSpace(country) ? "Proxy active" : country.Trim();
    }

    private async Task RefreshConnectionIdentityAsync(string lookupKey, string? proxyServer)
    {
        _proxyStatusCts?.Cancel();
        _proxyStatusCts?.Dispose();
        _proxyStatusCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = _proxyStatusCts.Token;

        ProxyEnrichment info;
        try
        {
            var tester = new ProxyListTester();
            info = string.IsNullOrWhiteSpace(proxyServer)
                ? await tester.EnrichDirectAsync(token)
                : await tester.EnrichAsync(proxyServer, token);
        }
        catch (Exception ex)
        {
            AppendLog($"[proxy-status] connection lookup failed: {ex.Message}");
            info = new ProxyEnrichment(string.Empty, string.Empty);
        }

        // Ignore a stale result if the active connection changed while we were looking it up.
        if (!string.Equals(lookupKey, _proxyStatusLookupKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _proxyStatusIp = info.Ip?.Trim() ?? string.Empty;
        _proxyStatusCountry = info.Country?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(_proxyStatusIp))
        {
            ApplyConnectionStatusVisual(
                !string.IsNullOrWhiteSpace(proxyServer),
                proxyServer ?? string.Empty,
                _proxyStatusIp,
                _proxyStatusCountry);
        }
        else if (string.IsNullOrWhiteSpace(proxyServer))
        {
            SetProxyStatusVisual("TextMutedBrush", "No proxy is used", "IP: Unknown");
        }
        else
        {
            // Reached nothing through the proxy — surface it so the user knows it is not working.
            SetProxyStatusVisual("DangerTextBrush", "Not responding", $"Proxy: {ProxyHostPortForDisplay(proxyServer)}");
        }
    }

    // Proxy host:port for the sidebar, with the scheme (and any credentials) stripped, e.g.
    // "socks5://144.91.111.48:1088" -> "144.91.111.48:1088".
    private static string ProxyHostPortForDisplay(string server)
    {
        var value = (server ?? string.Empty).Trim();
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        var rest = schemeIndex >= 0 ? value[(schemeIndex + 3)..] : value;
        var atIndex = rest.LastIndexOf('@');
        if (atIndex >= 0)
        {
            rest = rest[(atIndex + 1)..];
        }

        return rest;
    }

    private void SetProxyStatusVisual(string brushKey, string text, string? detail)
    {
        var brush = FindResource(brushKey) as System.Windows.Media.Brush;
        ProxyStatusIndicator.Fill = brush;
        ProxyStatusTextBlock.Text = text;
        ProxyStatusTextBlock.Foreground = brush;

        if (string.IsNullOrWhiteSpace(detail))
        {
            ProxyStatusDetailTextBlock.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            ProxyStatusDetailTextBlock.Text = detail;
            ProxyStatusDetailTextBlock.Visibility = System.Windows.Visibility.Visible;
        }
    }

    // Resolves the server speed multiplier (1x/3x/10x...) used to scale catalog build times. First tries
    // the configured server name (e.g. "Slangen 10x"), then the server URL where the speed lives in the
    // subdomain (e.g. "ts100.x10.america.travian.com"). Falls back to 1x and raises a one-time alarm when
    // no speed can be parsed. Estimates are best-effort and never block execution.
    private double ResolveServerSpeed()
    {
        var serverName = LoadBotOptions().ServerName;

        // Server name: digit before the x, e.g. "10x".
        var nameMatch = Regex.Match(serverName ?? string.Empty, @"(\d+)\s*[xX]");
        if (nameMatch.Success && double.TryParse(nameMatch.Groups[1].Value, out var nameSpeed) && nameSpeed > 0)
        {
            _serverSpeedAlarmRaised = false;
            return nameSpeed;
        }

        // Server URL: speed subdomain, x before the digits, e.g. ".x10.".
        var serverUrl = GetActiveAccountServerUrl();
        var urlMatch = Regex.Match(serverUrl ?? string.Empty, @"\.x(\d+)\b", RegexOptions.IgnoreCase);
        if (urlMatch.Success && double.TryParse(urlMatch.Groups[1].Value, out var urlSpeed) && urlSpeed > 0)
        {
            _serverSpeedAlarmRaised = false;
            return urlSpeed;
        }

        // No server configured at all (e.g. fresh install, no accounts added) — nothing to detect,
        // so stay silent instead of alarming. The alarm only matters when a server is set but unparseable.
        if (string.IsNullOrWhiteSpace(serverName) && string.IsNullOrWhiteSpace(serverUrl))
        {
            return 1.0;
        }

        if (!_serverSpeedAlarmRaised)
        {
            _serverSpeedAlarmRaised = true;
            AppendLog($"ALARM: could not detect server speed from server name '{serverName}' or URL '{serverUrl}'; using 1x for build estimates.");
        }

        return 1.0;
    }

    private string? GetActiveAccountServerUrl()
    {
        try
        {
            var account = _accountProvider.LoadAccount();
            return account.ServerUrl;
        }
        catch
        {
            return null;
        }
    }

}
