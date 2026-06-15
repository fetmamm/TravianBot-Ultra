namespace TbotUltra.Worker.Services;

/// <summary>
/// Stateless hero helpers extracted from <see cref="TravianClient"/>:
/// ointment dosing toward a target HP and hero stat-priority parsing/normalization.
/// Pure functions so they can be unit-tested in isolation.
/// </summary>
internal static class HeroCalc
{
    internal static int CalculateOintmentsToUse(int? currentHpPercent, int minHpForAdventure, int availableOintments)
    {
        if (currentHpPercent is null || availableOintments <= 0)
        {
            return 0;
        }

        var targetHp = Math.Clamp(minHpForAdventure, 1, 100);
        var currentHp = Math.Clamp(currentHpPercent.Value, 0, 100);
        if (currentHp >= targetHp)
        {
            return 0;
        }

        return Math.Min(targetHp - currentHp, availableOintments);
    }

    internal static IReadOnlyList<string> ParseHeroStatPriority(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fighting_strength"] = "fighting_strength",
            ["fighting strength"] = "fighting_strength",
            ["fight"] = "fighting_strength",
            ["strength"] = "fighting_strength",
            ["offence_bonus"] = "offence_bonus",
            ["offence bonus"] = "offence_bonus",
            ["offense_bonus"] = "offence_bonus",
            ["offense bonus"] = "offence_bonus",
            ["offence"] = "offence_bonus",
            ["offense"] = "offence_bonus",
            ["off"] = "offence_bonus",
            ["attack"] = "offence_bonus",
            ["defence_bonus"] = "defence_bonus",
            ["defence bonus"] = "defence_bonus",
            ["defense_bonus"] = "defence_bonus",
            ["defense bonus"] = "defence_bonus",
            ["defence"] = "defence_bonus",
            ["defense"] = "defence_bonus",
            ["def"] = "defence_bonus",
            ["resources"] = "resources",
            ["resource"] = "resources",
            ["production"] = "resources",
        };

        var parsed = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => map.GetValueOrDefault(item, string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallback in new[] { "resources", "fighting_strength", "offence_bonus", "defence_bonus" })
        {
            if (!parsed.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(fallback);
            }
        }

        return parsed;
    }
}
