namespace TbotUltra.Core.Release;

// Stable cross-process markers consumed by scripts/Test-ReleaseBundle.ps1. Keep the PowerShell
// contract test in ReleaseTemplateTests in sync when these values change.
public static class ReleaseSmokeContract
{
    public const string ReadyLogMarker = "[startup] Release smoke readiness completed.";
    public const string CatalogFailureLogMarker = "Construction cost/time estimates are disabled";
}
