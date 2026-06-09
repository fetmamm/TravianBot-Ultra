using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop.Services;

public static class AutomationExecutionOptions
{
    public static BotOptions WithoutImplicitVillageTarget(BotOptions source)
    {
        return BotOptionsFactory.CloneWithOverrides(
            source,
            targetVillageNameOverride: string.Empty,
            targetVillageUrlOverride: string.Empty);
    }
}
