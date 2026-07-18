namespace TbotUltra.Desktop.Models;

/// <summary>
/// One small status icon shown in the Dashboard village list (a building-queue slot or a
/// troop-training building). <see cref="IsActive"/> drives the dark (idle) vs bright (busy) look;
/// <see cref="Label"/> is the glyph/letter shown inside the slot and <see cref="Tooltip"/> explains it.
/// Rebuilt from the per-village status cache each time the village list is refreshed, so the type is a
/// plain immutable value (no change notification needed).
/// </summary>
public sealed record VillageActivitySlot
{
    public bool IsActive { get; init; }
    // Amber "waiting" state: the village has a deferred/blocked task for this icon (e.g. waiting for
    // resources or a full build queue) but nothing is actively running. Active (green) wins over waiting.
    public bool IsWaiting { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
}
