using System.IO;
using System.Text.Json;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public sealed class ServerCatalogStore
{
    private readonly string _catalogPath;

    public ServerCatalogStore(string catalogPath)
    {
        _catalogPath = catalogPath;
    }

    public List<ServerOption> Load()
    {
        if (!File.Exists(_catalogPath))
        {
            return [];
        }

        var raw = File.ReadAllText(_catalogPath);
        var entries = JsonSerializer.Deserialize<List<ServerOption>>(raw) ?? [];

        return entries
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BaseUrl))
            .Select(item => new ServerOption
            {
                Name = item.Name.Trim(),
                BaseUrl = item.BaseUrl.Trim().TrimEnd('/'),
            })
            .ToList();
    }

    public void Save(IEnumerable<ServerOption> servers)
    {
        var normalized = servers
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.BaseUrl))
            .Select(item => new ServerOption
            {
                Name = item.Name.Trim(),
                BaseUrl = item.BaseUrl.Trim().TrimEnd('/'),
            })
            .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directory = Path.GetDirectoryName(_catalogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_catalogPath, JsonSerializer.Serialize(normalized, options));
    }

    public void ResetToDefault()
    {
        if (File.Exists(_catalogPath))
        {
            File.Delete(_catalogPath);
        }
    }
}
