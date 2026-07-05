using System.IO;
using TbotUltra.Desktop.Services;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tbot-atomicfile-{Guid.NewGuid():N}");
    }

    [Fact]
    public void WriteAllText_CreatesFileWithExactContent()
    {
        var path = Path.Combine(_dir, "settings.json");

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Combine(_dir, "settings.json");
        AtomicFile.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_CreatesMissingParentDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deep", "file.txt");

        AtomicFile.WriteAllText(path, "x");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempResidue()
    {
        var path = Path.Combine(_dir, "settings.json");

        AtomicFile.WriteAllText(path, "content");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteAllText_RejectsEmptyPath(string path)
    {
        Assert.Throws<ArgumentException>(() => AtomicFile.WriteAllText(path, "x"));
    }

    [Fact]
    public async Task WriteAllText_RetriesThroughTransientLock()
    {
        var path = Path.Combine(_dir, "settings.json");
        AtomicFile.WriteAllText(path, "initial");

        // Hold an exclusive lock on the destination, release it shortly after. The write must retry
        // through the sharing violation instead of throwing (the OneDrive/AV transient-lock case).
        using var gate = new ManualResetEventSlim(false);
        var locker = Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            gate.Set();
            Thread.Sleep(80);
        });

        gate.Wait();
        AtomicFile.WriteAllText(path, "after-lock");
        await locker;

        Assert.Equal("after-lock", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ConcurrentWriters_ProduceOneCompleteWrite()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        var a = new string('a', 10000);
        var b = new string('b', 10000);

        Parallel.For(0, 4, i => AtomicFile.WriteAllText(path, i % 2 == 0 ? a : b));

        // The unique temp name means writers never share the temp file, so the swapped-in result is
        // always exactly one full write — never interleaved/partial content.
        var result = File.ReadAllText(path);
        Assert.True(result == a || result == b, "file content was interleaved/corrupted");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
