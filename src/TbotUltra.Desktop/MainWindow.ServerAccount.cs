using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TbotUltra.Core.Configuration;
using TbotUltra.Core.Travian;
using TbotUltra.Desktop.Models;

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

            var currentFlavor = config["server_flavor"]?.GetValue<string>() ?? string.Empty;
            var targetFlavor = ServerFlavorDetector.FromBaseUrl(targetUrl) == ServerFlavor.SsTravi
                ? "ss_travi"
                : "official";
            if (!string.Equals(currentFlavor, targetFlavor, StringComparison.OrdinalIgnoreCase))
            {
                config["server_flavor"] = targetFlavor;
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

    private async Task<List<ServerOption>> FetchDefaultServerOptionsAsync(BotOptions options)
    {
        try
        {
            var servers = await _serverDiscoveryService.FetchServersAsync();
            if (servers.Count > 0)
            {
                return servers;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Server discovery failed, using fallback: {ex.Message}");
        }

        return
        [
            new ServerOption
            {
                Name = options.ServerName,
                BaseUrl = options.BaseUrl,
            },
        ];
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
        }
        catch (Exception ex)
        {
            AppendLog($"Could not refresh account list: {ex.Message}");
        }
    }

    private void UpdateAccountInfoLabel(string accountName)
    {
        // Show only the username and a compact server-speed label (e.g. "slangen | 1M").
        var username = accountName;
        string? serverName = null;
        try
        {
            var account = _accountStore
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
            // Fall back to the raw account name / configured server below.
        }

        var serverLabel = AbbreviateServerSpeed(serverName ?? LoadBotOptions().ServerName);
        ActiveAccountInfoTextBlock.Text = string.IsNullOrWhiteSpace(serverLabel) || serverLabel == "-"
            ? username
            : $"{username} | {serverLabel}";
    }

    // Compacts a server-speed string into a short label: 1000000x -> "1M", 50000x -> "50K",
    // 10x -> "10x". Returns "-" when no speed can be parsed.
    private static string AbbreviateServerSpeed(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return "-";
        }

        var match = Regex.Match(serverName, @"(\d+)\s*[xX]");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var speed) || speed <= 0)
        {
            return "-";
        }

        if (speed >= 1_000_000)
        {
            return $"{(speed / 1_000_000d):0.##}M";
        }

        if (speed >= 1_000)
        {
            return $"{(speed / 1_000d):0.##}K";
        }

        return $"{speed}x";
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

    // Captchas only happen on SS-Travi servers, so the topbar "Captchas solved" card is shown only
    // when the active account points at an SS-Travi URL and stays hidden for official servers.
    private void UpdateCaptchaCardVisibility()
    {
        if (CaptchaStatsCard is null)
        {
            return;
        }

        var url = GetActiveAccountServerUrl();
        var isSsTravi = !string.IsNullOrWhiteSpace(url)
            && ServerFlavorDetector.FromBaseUrl(url) == ServerFlavor.SsTravi;
        CaptchaStatsCard.Visibility = isSsTravi
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private string ExtractServerSpeedLabel()
    {
        try
        {
            var serverName = LoadBotOptions().ServerName ?? string.Empty;
            var match = Regex.Match(serverName, @"(\d+)\s*[xX]");
            return match.Success ? $"{match.Groups[1].Value}x" : "-";
        }
        catch
        {
            return "-";
        }
    }
}
