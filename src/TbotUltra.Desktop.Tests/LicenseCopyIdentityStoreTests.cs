using TbotUltra.Desktop.Services.Licensing;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class LicenseCopyIdentityStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tbot-copy-id-{Guid.NewGuid():N}");

    [Fact]
    public void IdentityIsStableInOneFolderAndChangesWhenTheFolderIsCopied()
    {
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        Directory.CreateDirectory(firstRoot);

        var first = LicenseCopyIdentityStore.LoadOrCreate(firstRoot);
        var repeated = LicenseCopyIdentityStore.LoadOrCreate(firstRoot);
        Directory.CreateDirectory(Path.Combine(secondRoot, "config"));
        File.Copy(Path.Combine(firstRoot, "config", "license-copy.json"),
            Path.Combine(secondRoot, "config", "license-copy.json"));
        var copied = LicenseCopyIdentityStore.LoadOrCreate(secondRoot);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first.CopyId, copied.CopyId);
        Assert.Equal(first.MachineFingerprintHash, copied.MachineFingerprintHash);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
