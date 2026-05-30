namespace TbotUltra.Worker.Services;

/// <summary>
/// Holds session-scoped cache state that must survive across the short-lived <see cref="TravianClient"/>
/// instances created per operation for the same shared browser session/account. Without this, the
/// per-instance caches reset on every operation and cheap-but-repeated probes (Travian Plus / Gold
/// Club / tribe and the logged-in check) run again and again during the post-login burst.
/// </summary>
public sealed class TravianSessionCache
{
    public bool? CachedTravianPlusActive { get; set; }
    public bool? CachedGoldClubEnabled { get; set; }
    public string? SessionTribe { get; set; }
    public System.DateTimeOffset CachedTribePlusAt { get; set; } = System.DateTimeOffset.MinValue;

    public System.DateTimeOffset LastEnsureLoggedInAt { get; set; } = System.DateTimeOffset.MinValue;
    public bool LastEnsureLoggedInSucceeded { get; set; }
}
