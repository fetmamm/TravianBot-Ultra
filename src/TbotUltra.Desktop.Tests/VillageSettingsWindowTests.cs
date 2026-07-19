using Xunit;

namespace TbotUltra.Desktop.Tests;

// Runs on the shared WPF smoke thread: once any test creates Application.Current, constructing a
// Window on a second STA thread deadlocks against that Application's dispatcher.
[Collection(WpfSmokeCollection.Name)]
public sealed class VillageSettingsWindowTests
{
    private readonly WpfSmokeFixture _wpf;

    public VillageSettingsWindowTests(WpfSmokeFixture wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void Constructor_LoadsCompiledXamlWithNoVillages()
    {
        _wpf.Run(() =>
        {
            var window = new VillageSettingsWindow([]);
            window.Close();
        });
    }
}
