using TbotUltra.Core.Configuration;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Desktop.Services.Orchestration;

internal enum PreSleepFillHoldState
{
    Ignore,
    AwaitingDispatch,
    Running,
}

internal static class PreSleepFillHoldPolicy
{
    private static readonly TimeSpan DueTolerance = TimeSpan.FromSeconds(30);

    internal static PreSleepFillHoldState Evaluate(QueueItem item, DateTimeOffset now)
    {
        if (item.Group != QueueGroup.Construction)
        {
            return PreSleepFillHoldState.Ignore;
        }

        var isPreSleepFill = item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionPreSleepFill);
        var isLoginFill = item.Payload.ContainsKey(BotOptionPayloadKeys.ConstructionLoginFill);
        if (item.Status == QueueStatus.Running && (isPreSleepFill || isLoginFill))
        {
            return PreSleepFillHoldState.Running;
        }

        return item.Status == QueueStatus.Pending
            && isPreSleepFill
            && item.NextAttemptAt <= now + DueTolerance
                ? PreSleepFillHoldState.AwaitingDispatch
                : PreSleepFillHoldState.Ignore;
    }
}
