using System.Text.Json.Serialization;

namespace TbotUltra.Desktop.Services;

public sealed class AccountProxyPlan
{
    public bool Enabled { get; set; }
    public int VariationPercent { get; set; } = 30;
    public List<AccountProxyAssignment> Assignments { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsRotation => Enabled && Assignments.Select(item => item.ProxyId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    public AccountProxyPlan Clone() => new()
    {
        Enabled = Enabled,
        VariationPercent = VariationPercent,
        UpdatedAtUtc = UpdatedAtUtc,
        Assignments = Assignments.Select(item => item.Clone()).ToList(),
    };
}

public sealed class AccountProxyAssignment
{
    public string ProxyId { get; set; } = string.Empty;
    public List<ProxyTimeBlock> TimeBlocks { get; set; } = [];

    public AccountProxyAssignment Clone() => new()
    {
        ProxyId = ProxyId,
        TimeBlocks = TimeBlocks.Select(item => item.Clone()).ToList(),
    };
}

public sealed class ProxyTimeBlock
{
    public List<DayOfWeek> Days { get; set; } = [];
    public int StartHour { get; set; }
    public int EndHour { get; set; }
    public bool FullDay { get; set; }

    public ProxyTimeBlock Clone() => new()
    {
        Days = Days.ToList(),
        StartHour = StartHour,
        EndHour = EndHour,
        FullDay = FullDay,
    };
}

public sealed class AccountProxyRuntimeState
{
    public string ActiveProxyId { get; set; } = string.Empty;
    public string LastSuccessfulProxyId { get; set; } = string.Empty;
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public string RecoveryOverrideProxyId { get; set; } = string.Empty;
    public DateTimeOffset? RecoveryOverrideUntilUtc { get; set; }
}

public enum ProxyPlanIssueSeverity
{
    Warning,
    Error,
}

public sealed record ProxyPlanIssue(ProxyPlanIssueSeverity Severity, string Code, string Message);

public sealed record ProxyPlanValidationResult(IReadOnlyList<ProxyPlanIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != ProxyPlanIssueSeverity.Error);
    public IReadOnlyList<ProxyPlanIssue> Errors => Issues.Where(issue => issue.Severity == ProxyPlanIssueSeverity.Error).ToList();
    public IReadOnlyList<ProxyPlanIssue> Warnings => Issues.Where(issue => issue.Severity == ProxyPlanIssueSeverity.Warning).ToList();
}

public sealed record ProxyPlanResolution(
    string ProxyId,
    DateTimeOffset? NextTransitionAt,
    string NextProxyId,
    string Reason);
