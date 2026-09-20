using System.Globalization;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private string? DetectMainBuildingDurationAnomaly(
        int? gid,
        int targetLevel,
        int actualSeconds,
        string operation)
    {
        if (!_config.ConstructionMainBuildingRebuildEnabled || gid is null)
        {
            return null;
        }

        var serverSpeed = MainBuildingDurationAnomalyDetector.ResolveServerSpeed(
            _config.ServerName,
            _config.BaseUrl);
        var anomaly = MainBuildingDurationAnomalyDetector.Detect(
            gid.Value,
            targetLevel,
            actualSeconds,
            serverSpeed);
        if (anomaly is null)
        {
            return null;
        }

        Notify(
            $"[main-building] duration anomaly before {operation}: gid={gid.Value}, level={targetLevel}, "
            + $"actual={anomaly.ActualSeconds}s, healthyMaximum={anomaly.ExpectedMaximumSeconds:F1}s, "
            + $"ratio={anomaly.Ratio:F2}. Requesting Dorf2 verification before construction.");
        return "Main Building verification required: construction duration is unexpectedly high. "
            + $"main_building_duration_anomaly=true actual_seconds={anomaly.ActualSeconds} "
            + $"expected_max_seconds={Math.Ceiling(anomaly.ExpectedMaximumSeconds).ToString(CultureInfo.InvariantCulture)} "
            + "queue_wait_seconds=1";
    }
}
