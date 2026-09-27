using System.Text.Json.Nodes;

namespace TbotUltra.Core.Configuration;

internal static class SettingsDraftWriter
{
    internal static void WriteIntRange(
        JsonObject target,
        string minKey,
        string maxKey,
        int min,
        int max,
        int floor,
        int ceiling)
    {
        var normalizedMin = Math.Clamp(min, floor, ceiling);
        target[minKey] = normalizedMin;
        target[maxKey] = Math.Max(normalizedMin, Math.Clamp(max, floor, ceiling));
    }

    internal static void WriteDelayRange(
        JsonObject target,
        string minKey,
        string maxKey,
        double min,
        double max,
        double floor,
        double ceiling)
    {
        var normalizedMin = ClampFinite(min, floor, ceiling);
        target[minKey] = normalizedMin;
        target[maxKey] = Math.Max(normalizedMin, ClampFinite(max, floor, ceiling));
    }

    internal static double ClampFinite(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
