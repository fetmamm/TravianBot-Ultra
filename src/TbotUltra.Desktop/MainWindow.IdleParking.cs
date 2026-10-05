using TbotUltra.Core.Configuration;

namespace TbotUltra.Desktop;

public partial class MainWindow
{
    private async Task MaybeParkOnDorf2WhileIdleAsync(
        BotOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _botService.TryParkOnDorf2IfOnDorf1Async(
                    options,
                    AppendLog,
                    cancellationToken))
            {
                AppendLog("[idle-park] parked the current village on Dorf2 while automation waits.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"[idle-park] could not park on Dorf2; automation will continue: {ex.Message}");
        }
    }
}
