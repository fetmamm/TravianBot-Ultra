namespace TbotUltra.Desktop.Services;

public static class PostLoginRefreshDecisions
{
    public static bool ShouldReadCurrentVillageStatus(
        bool officialServer,
        bool newAccountAnalysisPending,
        bool newVillagesAnalyzed,
        bool newVillageAnalysisNavigated)
    {
        return !officialServer
            || !newAccountAnalysisPending
            || !newVillagesAnalyzed
            || newVillageAnalysisNavigated;
    }
}
