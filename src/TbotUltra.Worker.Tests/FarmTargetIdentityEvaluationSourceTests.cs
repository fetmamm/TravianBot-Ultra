using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class FarmTargetIdentityEvaluationSourceTests
{
    [Fact]
    public void IdentityDomResults_UseJsonElementTransport()
    {
        var root = ProjectRootLocator.FindProjectRoot();
        var sources = new[]
        {
            Path.Combine(
                root,
                "src",
                "TbotUltra.Worker",
                "Services",
                "Automation",
                "Core",
                "TravianClient.PlayerIdentity.cs"),
            Path.Combine(
                root,
                "src",
                "TbotUltra.Worker",
                "Services",
                "Automation",
                "Farming",
                "TravianClient.FarmAdd.cs"),
        };

        foreach (var path in sources)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("EvaluateAsync<FarmTargetIdentity>", source, StringComparison.Ordinal);
            Assert.Contains("EvaluateAsync<JsonElement>", source, StringComparison.Ordinal);
        }
    }
}
