using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TbotUltra.Desktop.Services;

public sealed class BotConfigStore
{
    private readonly string _configPath;

    public BotConfigStore(string configPath)
    {
        _configPath = configPath;
    }

    public JsonObject Load()
    {
        if (!File.Exists(_configPath))
        {
            throw new InvalidOperationException($"Config file not found: {_configPath}");
        }

        var raw = File.ReadAllText(_configPath);
        var node = JsonNode.Parse(raw)?.AsObject();
        if (node is null)
        {
            throw new InvalidOperationException("Config file is invalid JSON.");
        }

        return node;
    }

    public void Save(JsonObject config)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_configPath, config.ToJsonString(options));
    }
}
