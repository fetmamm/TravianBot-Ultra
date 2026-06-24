namespace TbotUltra.Worker.Services;

/// <summary>
/// Warehouse/granary storage capacities for a village. Promoted from a nested type on
/// <see cref="TravianClient"/> to a top-level record so the troop-training calculation
/// (<see cref="TroopTrainingCalculator"/>) and the NPC-trade code can share it.
/// </summary>
internal sealed record ResourceCapacitySnapshot(
    long? WarehouseCapacity,
    long? GranaryCapacity);
