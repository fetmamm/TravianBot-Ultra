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
