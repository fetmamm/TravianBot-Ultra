using TbotUltra.Worker.Services;
using Xunit;

namespace TbotUltra.Worker.Tests;

public sealed class StorageMasterPlanTreasuryParserTests
{
    private const string ActiveTreasury = """
        <h1 class="titleInHeader">Treasury <span class="level">Level 10</span></h1>
        <h4 class="round">Regions secured by your alliance</h4>
        <table class="show_artefacts"><tbody>
          <tr><td><i class="artefactIcon type9"></i></td>
              <td>Slight storage masterplan IX</td>
              <td class="al">Ancient power activated for this village.</td></tr>
        </tbody></table>
        <div class="allianceArtifacts"><table class="show_artefacts"></table></div>
        """;

    [Fact]
    public void ActiveVillagePower_IsConfirmed()
        => Assert.True(StorageMasterPlanTreasuryParser.ParseActiveVillageEffect(ActiveTreasury));

    [Fact]
    public void AllianceAvailablePower_DoesNotCountAsActivated()
    {
        var html = """
            <h1 class="titleInHeader">Treasury</h1>
            <h4>Regions secured by your alliance</h4>
            <table class="show_artefacts"><tbody></tbody></table>
            <div class="allianceArtifacts"><table class="show_artefacts">
              <tr><td><i class="artefactIcon type9"></i></td>
                  <td>Slight storage masterplan IX</td><td class="al">Activate</td></tr>
            </table></div>
            """;

        Assert.False(StorageMasterPlanTreasuryParser.ParseActiveVillageEffect(html));
    }

    [Fact]
    public void MissingAuthoritativeSection_IsUnknown()
        => Assert.Null(StorageMasterPlanTreasuryParser.ParseActiveVillageEffect(
            "<h1>Treasury</h1><table class='show_artefacts'></table>"));

    [Fact]
    public void OtherActivePower_DoesNotCount()
        => Assert.False(StorageMasterPlanTreasuryParser.ParseActiveVillageEffect(
            ActiveTreasury.Replace("storage masterplan", "construction bonus", StringComparison.OrdinalIgnoreCase)));
}
