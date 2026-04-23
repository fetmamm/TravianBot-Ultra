using Microsoft.Extensions.Options;
using TbotUltra.Worker;
using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Services;

var projectRoot = ProjectRootLocator.FindProjectRoot();
var botConfigPath = Path.Combine(projectRoot, "config", "bot.json");
var envPath = Path.Combine(projectRoot, ".env");
var queuePath = Path.Combine(projectRoot, "config", "queue.json");

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(botConfigPath, optional: false, reloadOnChange: true);

builder.Services
    .AddOptions<BotOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IAccountProvider>(new EnvAccountProvider(envPath));
builder.Services.AddSingleton(new ProjectContext(projectRoot));
builder.Services.AddSingleton<IQueueStore>(new JsonQueueStore(queuePath));
builder.Services.AddSingleton<IQueueScheduler, PriorityFifoQueueScheduler>();
builder.Services.AddSingleton<BotTaskRunner>();
builder.Services.AddSingleton<QueueExecutor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
