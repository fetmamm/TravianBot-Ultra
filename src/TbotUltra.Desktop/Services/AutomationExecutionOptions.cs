using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

internal static class AutomationExecutionOptions
{
    public static BotOptions WithoutImplicitVillageTarget(BotOptions source)
    {
        return BotOptionsPayloadApplier.Apply(source, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BotOptionPayloadKeys.TargetVillageName] = string.Empty,
            [BotOptionPayloadKeys.TargetVillageUrl] = string.Empty,
        });
    }
}
