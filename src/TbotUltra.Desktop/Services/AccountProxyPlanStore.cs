using System.IO;
using System.Text.Json;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

public sealed class AccountProxyPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _projectRoot;

    public AccountProxyPlanStore(string projectRoot)
    {
        _projectRoot = projectRoot;
    }

    public AccountProxyPlan? LoadActive(string accountName) => Load(AccountStoragePaths.ProxyPlanPath(_projectRoot, accountName));

    public AccountProxyPlan? LoadDraft(string accountName) => Load(AccountStoragePaths.ProxyPlanDraftPath(_projectRoot, accountName));

    public AccountProxyRuntimeState LoadRuntime(string accountName)
    {
        var path = AccountStoragePaths.ProxyRuntimeStatePath(_projectRoot, accountName);
        return LoadJson<AccountProxyRuntimeState>(path) ?? new AccountProxyRuntimeState();
    }

    public void SaveActive(string accountName, AccountProxyPlan plan)
    {
        plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
        Save(AccountStoragePaths.ProxyPlanPath(_projectRoot, accountName), plan);
    }

    public void SaveDraft(string accountName, AccountProxyPlan plan)
    {
        plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
        Save(AccountStoragePaths.ProxyPlanDraftPath(_projectRoot, accountName), plan);
    }

    public void SaveRuntime(string accountName, AccountProxyRuntimeState state)
        => AtomicFile.WriteAllText(
            AccountStoragePaths.ProxyRuntimeStatePath(_projectRoot, accountName),
            JsonSerializer.Serialize(state, JsonOptions));

    public void DeleteDraft(string accountName)
    {
        var path = AccountStoragePaths.ProxyPlanDraftPath(_projectRoot, accountName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public AccountProxyPlan BuildLegacyPlan(string? proxyServer, IEnumerable<ProxyLibraryEntry> library)
    {
        var proxy = ProxyLibraryStore.FindByServer(library, proxyServer);
        if (proxy is null)
        {
            return new AccountProxyPlan();
        }

        return new AccountProxyPlan
        {
            Enabled = true,
            Assignments =
            [
                new AccountProxyAssignment
                {
                    ProxyId = proxy.Id,
                    TimeBlocks =
                    [
                        new ProxyTimeBlock
                        {
                            Days = Enum.GetValues<DayOfWeek>().ToList(),
                            FullDay = true,
                        },
                    ],
                },
            ],
        };
    }

    private static AccountProxyPlan? Load(string path) => LoadJson<AccountProxyPlan>(path);

    private static T? LoadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        if (value is AccountProxyPlan plan)
        {
            plan.VariationPercent = Math.Clamp(plan.VariationPercent, 0, 49);
            plan.Assignments ??= [];
        }

        return value;
    }

    private static void Save(string path, AccountProxyPlan plan)
        => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(plan, JsonOptions));
}
