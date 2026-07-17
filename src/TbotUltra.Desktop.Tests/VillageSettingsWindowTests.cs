using System;
using System.Threading;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class VillageSettingsWindowTests
{
    [Fact]
    public void Constructor_LoadsCompiledXaml()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new VillageSettingsWindow([]);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Window construction timed out.");
        Assert.Null(failure);
    }
}
