using System.Text.Json.Nodes;
using TbotUltra.Core.Configuration;
using TbotUltra.Desktop.Models;
using TbotUltra.Desktop.Services;
using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private AccountProxyPlanStore ProxyPlanStore => new(_projectRoot);

    private void ConfigureProxyPlanTransition(string accountName)
    {
        try
        {
            var plan = ProxyPlanStore.LoadActive(accountName);
            if (plan?.Enabled != true || !plan.IsRotation)
            {
                _sessionPacer.SetNextProxyTransition(null);
                return;
            }

            var runtime = ProxyPlanStore.LoadRuntime(accountName);
            var resolution = AccountProxyPlanResolver.Resolve(plan, accountName, DateTimeOffset.Now, runtime);
            var next = resolution.NextTransitionAt;
            _sessionPacer.SetNextProxyTransition(next);
            if (next is not null)
            {
                AppendLog($"[proxy-plan:verbose] next varied proxy boundary at {next.Value:yyyy-MM-dd HH:mm} local time.");
            }
        }
        catch (Exception ex)
        {
            _sessionPacer.SetNextProxyTransition(null);
            AppendLog($"[proxy-plan] could not configure next transition: {ex.Message}");
        }
    }

    private bool PrepareProxyPlanForLogin()
    {
        var account = FindAccount(_accountStore.ActiveAccountName());
        if (account is null)
        {
            return true;
        }

        try
        {
            var plan = ProxyPlanStore.LoadActive(account.Name);
            if (plan?.Enabled != true)
            {
                return true;
            }

            var config = _botConfigStore.LoadForAccount(account.Name);
            var library = new ProxyLibraryStore().Load();
            var validation = ValidateProxyPlan(plan, library, account, config, requireHealth: false);
            if (!validation.IsValid)
            {
                var message = string.Join(" ", validation.Errors.Select(issue => issue.Message));
                AppendLog($"[ALARM] [proxy-plan] login blocked: {message}");
                StatusTextBlock.Text = "Proxy setup is invalid. Login blocked.";
                return false;
            }

            return ApplyResolvedProxy(account, plan, library, DateTimeOffset.Now, "login");
        }
        catch (Exception ex)
        {
            AppendLog($"[ALARM] [proxy-plan] login validation failed: {ex.Message}");
            StatusTextBlock.Text = "Proxy setup could not be validated. Login blocked.";
            return false;
        }
    }

    private async Task ApplyProxyPlanForWakeAsync(DateTimeOffset wakeAt)
    {
        var account = FindAccount(_accountStore.ActiveAccountName());
        if (account is null)
        {
            return;
        }

        var plan = ProxyPlanStore.LoadActive(account.Name);
        if (plan?.Enabled != true)
        {
            return;
        }

        var library = new ProxyLibraryStore().Load();
        var config = _botConfigStore.LoadForAccount(account.Name);
        var validation = ValidateProxyPlan(plan, library, account, config, requireHealth: false);
        if (!validation.IsValid)
        {
            AppendLog($"[ALARM] [proxy-plan] scheduled switch blocked: {string.Join(" ", validation.Errors.Select(issue => issue.Message))}");
            return;
        }

        var runtime = ProxyPlanStore.LoadRuntime(account.Name);
        runtime.RecoveryOverrideProxyId = string.Empty;
        runtime.RecoveryOverrideUntilUtc = null;
        ProxyPlanStore.SaveRuntime(account.Name, runtime);
        var resolution = AccountProxyPlanResolver.Resolve(plan, account.Name, wakeAt, runtime);
        if (string.IsNullOrWhiteSpace(resolution.ProxyId))
        {
            if (!account.ProxyEnabled)
            {
                return;
            }
        }
        var target = library.FirstOrDefault(proxy => string.Equals(proxy.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));
        if (target is not null && account.ProxyEnabled
            && string.Equals(account.ProxyServer.Trim(), target.Server.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _botService.ShutdownAsync(AppendLog);
        }
        catch (Exception ex)
        {
            AppendLog($"[proxy-plan] browser shutdown before scheduled switch failed: {ex.Message}");
        }

        ApplyResolvedProxy(account, plan, library, wakeAt, "scheduled sleep wake");
    }

    private bool ApplyResolvedProxy(
        AccountEntry account,
        AccountProxyPlan plan,
        IReadOnlyCollection<ProxyLibraryEntry> library,
        DateTimeOffset at,
        string reason)
    {
        var runtime = ProxyPlanStore.LoadRuntime(account.Name);
        var resolution = AccountProxyPlanResolver.Resolve(plan, account.Name, at, runtime);
        if (string.IsNullOrWhiteSpace(resolution.ProxyId))
        {
            if (account.NeverUseOwnIp)
            {
                AppendLog($"[ALARM] [proxy-plan] {reason} resolved to direct connection while Never use own IP is enabled.");
                return false;
            }

            if (account.ProxyEnabled)
            {
                var changed = CloneAccount(account);
                changed.ProxyEnabled = false;
                _accountStore.SaveAccount(changed, setActive: false);
                AppendLog($"[proxy-plan] {reason}: no proxy is scheduled; using the allowed direct connection.");
            }

            runtime.ActiveProxyId = string.Empty;
            runtime.ActivatedAtUtc = DateTimeOffset.UtcNow;
            ProxyPlanStore.SaveRuntime(account.Name, runtime);
            return true;
        }

        var target = library.FirstOrDefault(proxy => string.Equals(proxy.Id, resolution.ProxyId, StringComparison.OrdinalIgnoreCase));
        if (target is null || !ProxyParser.TryBuild(target.Server, out _, out _))
        {
            AppendLog($"[ALARM] [proxy-plan] {reason} could not resolve a valid proxy.");
            return !account.NeverUseOwnIp;
        }

        if (!string.Equals(account.ProxyServer.Trim(), target.Server.Trim(), StringComparison.OrdinalIgnoreCase) || !account.ProxyEnabled)
        {
            var changed = CloneAccount(account);
            changed.ProxyEnabled = true;
            changed.ProxyServer = target.Server;
            _accountStore.SaveAccount(changed, setActive: false);
            AppendLog($"[proxy-plan] {reason}: activated {ProxyParser.MaskForLog(target.Server)}.");
        }

        runtime.ActiveProxyId = target.Id;
        runtime.LastSuccessfulProxyId = target.Id;
        runtime.ActivatedAtUtc = DateTimeOffset.UtcNow;
        ProxyPlanStore.SaveRuntime(account.Name, runtime);
        return true;
    }

    private static ProxyPlanValidationResult ValidateProxyPlan(
        AccountProxyPlan plan,
        IReadOnlyCollection<ProxyLibraryEntry> library,
        AccountEntry account,
        JsonObject config,
        bool requireHealth)
    {
        var allowed = config[BotOptionPayloadKeys.SessionPacingAllowedHours] is JsonArray array
            ? array.Select(node => node?.GetValue<int>() ?? -1).Where(hour => hour is >= 0 and <= 23).ToArray()
            : Enumerable.Range(0, 24).ToArray();
        var pacingEnabled = config[BotOptionPayloadKeys.SessionPacingEnabled]?.GetValue<bool>() ?? PacingDefaults.SessionPacingEnabled;
        var sleepMin = config[BotOptionPayloadKeys.SessionPacingSleepMinMinutes]?.GetValue<int>() ?? PacingDefaults.SessionPacingSleepMinMinutes;
        return AccountProxyPlanValidator.Validate(
            plan,
            library,
            account.Name,
            account.NeverUseOwnIp,
            pacingEnabled,
            allowed,
            sleepMin,
            requireHealth);
    }

    private string? ValidateActiveProxyPlanForSettings(JsonObject candidateConfig)
    {
        try
        {
            var account = FindAccount(_accountStore.ActiveAccountName());
            var plan = account is null ? null : ProxyPlanStore.LoadActive(account.Name);
            if (account is null || plan?.Enabled != true)
            {
                return null;
            }

            var result = ValidateProxyPlan(plan, new ProxyLibraryStore().Load(), account, candidateConfig, requireHealth: false);
            if (result.IsValid)
            {
                return null;
            }

            return "These Session pacing changes would make the active proxy setup unsafe:\n\n"
                + string.Join("\n", result.Errors.Select(issue => $"• {issue.Message}"))
                + "\n\nAdjust the proxy schedule in Manage accounts first.";
        }
        catch (Exception ex)
        {
            return $"The active proxy setup could not be validated: {ex.Message}";
        }
    }
}
