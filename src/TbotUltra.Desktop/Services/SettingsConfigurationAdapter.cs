using System.Text;
using System.Text.Json.Nodes;
using System.IO;
using Microsoft.Extensions.Configuration;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

internal static class SettingsConfigurationAdapter
{
    internal static SettingsConfiguration Load(JsonObject source)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source.ToJsonString()));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        return SettingsConfigurationProjection.FromConfiguration(configuration);
    }

    internal static JsonObject BuildDraft(JsonObject source, SettingsConfiguration settings)
        => SettingsConfigurationProjection.Apply(source, settings);
}
