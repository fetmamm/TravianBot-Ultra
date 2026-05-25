using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class ResourceTransferCalculationTests
{
    [Fact]
    public void CalculateResourceTransferShipment_SendsSurplusUpToTargetSpace()
    {
        var source = Resources(900, 860, 400, 950);
        var target = Resources(100, 300, 100, 500);

        var shipment = TravianClient.CalculateResourceTransferShipment(
            source,
            sourceWarehouseCapacity: 1000,
            sourceGranaryCapacity: 1000,
            target,
            targetWarehouseCapacity: 1000,
            targetGranaryCapacity: 1000,
            enabledResources: ["wood", "clay", "iron", "crop"],
            sourceThresholdPercent: 85,
            sourceKeepPercent: 70,
            targetFillPercent: 90,
            merchantCapacity: 5000);

        Assert.Equal(200, shipment.Wood);
        Assert.Equal(160, shipment.Clay);
        Assert.Equal(0, shipment.Iron);
        Assert.Equal(250, shipment.Crop);
    }

    [Fact]
    public void CalculateResourceTransferShipment_SkipsDisabledResourcesAndFullTarget()
    {
        var shipment = TravianClient.CalculateResourceTransferShipment(
            Resources(950, 950, 950, 950),
            sourceWarehouseCapacity: 1000,
            sourceGranaryCapacity: 1000,
            Resources(100, 900, 100, 900),
            targetWarehouseCapacity: 1000,
            targetGranaryCapacity: 1000,
            enabledResources: ["wood", "crop"],
            sourceThresholdPercent: 85,
            sourceKeepPercent: 70,
            targetFillPercent: 90,
            merchantCapacity: 5000);

        Assert.Equal(250, shipment.Wood);
        Assert.Equal(0, shipment.Clay);
        Assert.Equal(0, shipment.Iron);
        Assert.Equal(0, shipment.Crop);
    }

    [Fact]
    public void CalculateResourceTransferShipment_ScalesToMerchantCapacity()
    {
        var shipment = TravianClient.CalculateResourceTransferShipment(
            Resources(950, 950, 950, 950),
            sourceWarehouseCapacity: 1000,
            sourceGranaryCapacity: 1000,
            Resources(100, 100, 100, 100),
            targetWarehouseCapacity: 1000,
            targetGranaryCapacity: 1000,
            enabledResources: ["wood", "clay", "iron", "crop"],
            sourceThresholdPercent: 85,
            sourceKeepPercent: 70,
            targetFillPercent: 90,
            merchantCapacity: 500);

        Assert.Equal(125, shipment.Wood);
        Assert.Equal(125, shipment.Clay);
        Assert.Equal(125, shipment.Iron);
        Assert.Equal(125, shipment.Crop);
        Assert.Equal(500, shipment.Total);
    }

    private static Dictionary<string, long> Resources(long wood, long clay, long iron, long crop)
    {
        return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = wood,
            ["clay"] = clay,
            ["iron"] = iron,
            ["crop"] = crop,
        };
    }
}
