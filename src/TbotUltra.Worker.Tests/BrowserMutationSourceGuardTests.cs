using System.Text.RegularExpressions;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class BrowserMutationSourceGuardTests
{
    private static readonly IReadOnlyDictionary<string, int> LegacyMutationCeilings =
        new Dictionary<string, int>
        {
            [@"\.ClickAsync\("] = 37,
            [@"\.FillAsync\("] = 10,
            [@"\.PressAsync\("] = 3,
            [@"\.PressSequentiallyAsync\("] = 1,
            [@"\.SelectOptionAsync\("] = 6,
            [@"\.CheckAsync\("] = 0,
            [@"\.UncheckAsync\("] = 0,
            [@"\.DispatchEventAsync\("] = 1,
            [@"\.click\(\)"] = 52,
            [@"dispatchEvent\("] = 60,
        };

    [Fact]
    public void RawNavigationAndReload_AreLimitedToTraceAdapters()
    {
        var root = FindRepositoryRoot();
        var automationRoot = Path.Combine(root, "src", "TbotUltra.Worker", "Services", "Automation");
        var violations = Directory.GetFiles(automationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("TravianClient.Navigation.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("TravianClient.Catapults.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("TravcoInactiveSearch.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, number: index + 1)))
            .Where(item => Regex.IsMatch(item.line, @"\.(?:GotoAsync|ReloadAsync)\("))
            .Select(item => $"{Path.GetRelativePath(root, item.path)}:{item.number}")
            .ToArray();

        Assert.True(violations.Length == 0, "Raw navigation/reload outside trace adapter: " + string.Join(", ", violations));
    }

    [Fact]
    public void NewRawDomMutations_CannotIncreaseLegacySurface()
    {
        var root = FindRepositoryRoot();
        var automationRoot = Path.Combine(root, "src", "TbotUltra.Worker", "Services", "Automation");
        var source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(automationRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));

        foreach (var (pattern, ceiling) in LegacyMutationCeilings)
        {
            var count = Regex.Matches(source, pattern).Count;
            Assert.True(count <= ceiling, $"Raw mutation '{pattern}' increased from ceiling {ceiling} to {count}. Use the traced browser action path.");
        }

        var browserSession = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Worker", "Infrastructure", "BrowserSession.cs"));
        Assert.Contains(DomTraceConsolePrefix, browserSession);
        var traceLogger = File.ReadAllText(Path.Combine(root, "src", "TbotUltra.Worker", "Infrastructure", "BrowserTraceLogger.cs"));
        Assert.Contains(DomTraceConsolePrefix, traceLogger);
        Assert.Contains("addEventListener('click'", browserSession);
        Assert.Contains("addEventListener('change'", browserSession);
        Assert.Contains("addEventListener('submit'", browserSession);
        Assert.Contains("const observerTarget = document.documentElement", browserSession);
        Assert.Contains("if (!observerTarget) return", browserSession);
    }

    [Fact]
    public void TribeDetectionBrowserScript_HasBalancedFunctionBody()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "src",
            "TbotUltra.Worker",
            "Services",
            "Automation",
            "Core",
            "TravianClient.AccountSnapshot.cs");
        var source = File.ReadAllText(path);
        var match = Regex.Match(
            source,
            "DetectTribeFromCurrentPageAsync[\\s\\S]*?EvaluateAsync<string>\\(\\s*\"\"\"(?<script>[\\s\\S]*?)\"\"\"",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, "Could not locate the tribe detection browser script.");
        var script = match.Groups["script"].Value;
        Assert.Equal(script.Count(character => character == '{'), script.Count(character => character == '}'));
    }

    private const string DomTraceConsolePrefix = "__TBOT_BROWSER_TRACE__";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TbotUltra.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate TbotUltra.sln from the test output directory.");
    }
}
