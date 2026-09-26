using TbotUltra.Worker.Infrastructure;

namespace TbotUltra.Worker.Services;

internal sealed class BrowserSessionBonusVideoRunner(
    BrowserSession session,
    TravianSessionCache sessionCache,
    bool interactive) : IIsolatedBonusVideoRunner
{
    public IIsolatedBonusVideoOperation BeginOperation()
        => session.BeginIsolatedBonusVideoOperation(sessionCache, interactive);
}
