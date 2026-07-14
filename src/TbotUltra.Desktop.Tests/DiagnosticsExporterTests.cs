using System.IO.Compression;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class DiagnosticsExporterTests
{
    [Fact]
    public async Task CreateAsync_IncludesAllSourcesAndSanitizesText()
    {
        using var fixture = new DiagnosticsFixture();
        var projectLogPath = fixture.WriteProjectFile("logs/session.txt", "account=user_gmail_com_server password=secret");
        fixture.WriteAppFile("logs/desktop-unhandled.log", "person@example.com failed");
        fixture.WriteProjectFile("config/bot.json", "{\"password\":\"secret\",\"base_url\":\"https://example.invalid\"}");
        fixture.WriteProjectFile("config/accounts/user_gmail_com_server/settings.json", "{\"email\":\"person@example.com\",\"enabled\":true}");
        fixture.WriteProjectFile("config/accounts/user_gmail_com_server/session/playwright-state.json", "{\"token\":\"must-not-exist\"}");
        fixture.WriteProjectFile(".env", "PASSWORD=must-not-exist");
        fixture.WriteProjectFile("temp_build_out/diagnostics/page.html", "<p>person@example.com</p>");
        fixture.WriteProjectBytes("temp_build_out/diagnostics/page.png", [1, 2, 3, 4]);

        await using var openLog = new FileStream(projectLogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = await fixture.Exporter.CreateAsync(fixture.Request(["terminal person@example.com"]));

        Assert.True(File.Exists(result.ZipPath));
        using var archive = ZipFile.OpenRead(result.ZipPath);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("diagnostics.txt", entryNames);
        Assert.Contains("current-terminal.txt", entryNames);
        Assert.Contains("logs/session.txt", entryNames);
        Assert.Contains("logs/desktop-unhandled.log", entryNames);
        Assert.Contains("configuration/global/bot.json", entryNames);
        Assert.Contains("configuration/accounts/account-001/settings.json", entryNames);
        Assert.Contains("runtime-diagnostics/page.html", entryNames);
        Assert.Contains("runtime-diagnostics/page.png", entryNames);
        Assert.DoesNotContain(entryNames, name => name.Contains("playwright-state", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entryNames, name => name.EndsWith(".env", StringComparison.OrdinalIgnoreCase));

        var allText = string.Join("\n", archive.Entries
            .Where(entry => entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Select(ReadEntry));
        Assert.DoesNotContain("person@example.com", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("password=secret", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("user_gmail_com_server", allText, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-exist", allText, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid", allText, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot, ".*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CreateAsync_DeduplicatesSameLogDirectory()
    {
        using var fixture = new DiagnosticsFixture(useProjectRootAsAppRoot: true);
        fixture.WriteProjectFile("logs/session.txt", "one log");

        var result = await fixture.Exporter.CreateAsync(fixture.Request([]));

        using var archive = ZipFile.OpenRead(result.ZipPath);
        Assert.Single(archive.Entries, entry => entry.FullName == "logs/session.txt");
    }

    [Fact]
    public async Task CreateAsync_RemovesTemporaryFilesWhenARequiredLogCannotBeRead()
    {
        using var fixture = new DiagnosticsFixture();
        var lockedPath = fixture.WriteProjectFile("logs/locked.log", "locked");
        await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => fixture.Exporter.CreateAsync(fixture.Request([])));

        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputRoot));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class DiagnosticsFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-diagnostics-tests-{Guid.NewGuid():N}");

        internal DiagnosticsFixture(bool useProjectRootAsAppRoot = false)
        {
            ProjectRoot = Path.Combine(_root, "project");
            AppRoot = useProjectRootAsAppRoot ? ProjectRoot : Path.Combine(_root, "app");
            OutputRoot = Path.Combine(_root, "output");
            Directory.CreateDirectory(ProjectRoot);
            Directory.CreateDirectory(AppRoot);
            Directory.CreateDirectory(OutputRoot);
        }

        internal DiagnosticsExporter Exporter { get; } = new();
        internal string ProjectRoot { get; }
        internal string AppRoot { get; }
        internal string OutputRoot { get; }

        internal DiagnosticsExportRequest Request(IReadOnlyList<string> terminalEntries) => new(
            ProjectRoot,
            AppRoot,
            OutputRoot,
            "1.2.3",
            terminalEntries,
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

        internal string WriteProjectFile(string relativePath, string content)
            => WriteFile(ProjectRoot, relativePath, content);

        internal string WriteAppFile(string relativePath, string content)
            => WriteFile(AppRoot, relativePath, content);

        internal void WriteProjectBytes(string relativePath, byte[] content)
        {
            var path = Path.Combine(ProjectRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        private static string WriteFile(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
