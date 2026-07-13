namespace TbotUltra.Desktop.Services;

public static class AccountProxyPlanNormalizer
{
    public static AccountProxyPlan Normalize(AccountProxyPlan source)
    {
        var result = source.Clone();
        result.VariationPercent = Math.Clamp(result.VariationPercent, 0, 49);
        result.Assignments = result.Assignments
            .Where(assignment => !string.IsNullOrWhiteSpace(assignment.ProxyId))
            .GroupBy(assignment => assignment.ProxyId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AccountProxyAssignment
            {
                ProxyId = group.Key,
                TimeBlocks = NormalizeBlocks(group.SelectMany(item => item.TimeBlocks)),
            })
            .ToList();
        return result;
    }

    private static List<ProxyTimeBlock> NormalizeBlocks(IEnumerable<ProxyTimeBlock> blocks)
    {
        var result = new List<ProxyTimeBlock>();
        foreach (var dayGroup in blocks
            .Select(block => block.Clone())
            .GroupBy(block => string.Join(',', block.Days.Distinct().OrderBy(day => (int)day))))
        {
            var days = dayGroup.SelectMany(block => block.Days).Distinct().OrderBy(day => (int)day).ToList();
            if (dayGroup.Any(block => block.FullDay))
            {
                result.Add(new ProxyTimeBlock { Days = days, FullDay = true });
                continue;
            }

            var simple = dayGroup
                .Where(block => block.StartHour < block.EndHour)
                .OrderBy(block => block.StartHour)
                .ThenBy(block => block.EndHour)
                .ToList();
            foreach (var block in simple)
            {
                var previous = result.LastOrDefault(item => !item.FullDay
                    && item.StartHour < item.EndHour
                    && item.Days.SequenceEqual(days));
                if (previous is not null && block.StartHour <= previous.EndHour)
                {
                    previous.EndHour = Math.Max(previous.EndHour, block.EndHour);
                }
                else
                {
                    block.Days = days.ToList();
                    result.Add(block);
                }
            }

            result.AddRange(dayGroup
                .Where(block => block.StartHour > block.EndHour)
                .GroupBy(block => (block.StartHour, block.EndHour))
                .Select(group => new ProxyTimeBlock
                {
                    Days = days.ToList(),
                    StartHour = group.Key.StartHour,
                    EndHour = group.Key.EndHour,
                }));
        }

        return result;
    }
}
