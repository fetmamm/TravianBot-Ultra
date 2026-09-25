namespace TbotUltra.Core.Configuration;

public static class BotOptionsPayloadApplier
{
    public static BotOptions Apply(BotOptions source, IReadOnlyDictionary<string, string>? payload)
    {
        var construction = ConstructionOptionsModule.Apply(source, payload);
        var hero = HeroOptionsModule.Apply(source, payload);
        var farming = FarmingOptionsModule.Apply(source, payload);
        var postLogin = PostLoginOptionsModule.Apply(source, payload);
        var troopTraining = TroopTrainingOptionsModule.Apply(source, payload);
        var npcTrade = NpcTradeOptionsModule.Apply(source, payload);
        var resourceTransfer = ResourceTransferOptionsModule.Apply(source, payload);
        var reinforcements = ReinforcementOptionsModule.Apply(source, payload);
        var actionPacing = ActionPacingOptionsModule.Apply(source, payload);

        var options = construction.ApplyTo(source);

        // Village payloads can change training tasks, but the fallback cooldown remains
        // an account-level setting owned by the Settings configuration.
        options = farming.ApplyTo(options);
        options = postLogin.ApplyTo(options);
        options = troopTraining.ApplyTo(options, preserveAccountSettings: true);
        options = npcTrade.ApplyTo(options);
        options = resourceTransfer.ApplyTo(options);
        options = reinforcements.ApplyTo(options);
        options = actionPacing.ApplyTo(options);
        return hero.ApplyTo(options);
    }

}
