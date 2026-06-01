using Microsoft.Extensions.Options;
using TbotUltra.Core.Accounts;
using TbotUltra.Worker;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;

var projectRoot = ProjectRootLocator.FindProjectRoot();
var botConfigPath = Path.Combine(projectRoot, "config", "bot.json");
var envPath = Path.Combine(projectRoot, ".env");
var queuePath = Path.Combine(projectRoot, "config", "queue.json");

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(botConfigPath, optional: false, reloadOnChange: true);
var activeAccountName = ResolveActiveAccountName(envPath);
if (!string.IsNullOrWhiteSpace(activeAccountName))
{
    builder.Configuration.AddJsonFile(
        AccountStoragePaths.AccountSettingsPath(projectRoot, activeAccountName),
        optional: true,
        reloadOnChange: true);
}

builder.Services
    .AddOptions<BotOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAccountProvider>(new EnvAccountProvider(envPath));
builder.Services.AddSingleton(new ProjectContext(projectRoot));
builder.Services.AddSingleton<ICaptchaAutoSolver, CaptchaAutoSolver>();
builder.Services.AddSingleton<IQueueStore>(new JsonQueueStore(queuePath));
builder.Services.AddSingleton<IQueueScheduler, PriorityFifoQueueScheduler>();
builder.Services.AddSingleton<BotTaskRunner>();
builder.Services.AddSingleton<QueueExecutor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

static string ResolveActiveAccountName(string envPath)
{
    var values = EnvFileParser.ReadValues(envPath);
    var accounts = values.GetValueOrDefault("TBOT_ACCOUNTS", string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToList();
    var active = Environment.GetEnvironmentVariable("TBOT_ACTIVE_ACCOUNT")
        ?? values.GetValueOrDefault("TBOT_ACTIVE_ACCOUNT", string.Empty);

    if (!string.IsNullOrWhiteSpace(active)
        && accounts.Contains(active, StringComparer.OrdinalIgnoreCase))
    {
        return active;
    }

    return accounts.FirstOrDefault() ?? string.Empty;
}
