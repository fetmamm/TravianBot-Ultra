using TbotUltra.Desktop.Services.Logging;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LogClassifierTests
{
    [Theory]
    [InlineData("Hero status: dead=False, hp=?%, adventures=0, points=0, in_village=True. No hero action was needed.")]
    [InlineData("[hero_manage STARTED] (1/1) on 'account'")]
    [InlineData("[LoginAsync started]")]
    public void IsVerbose_HidesRequestedDiagnosticLinesInCleanMode(string message)
    {
        Assert.True(LogClassifier.IsVerbose(message));
    }
}
