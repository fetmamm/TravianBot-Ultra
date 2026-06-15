namespace TbotUltra.Worker.Tests;

internal static class TestDomFixtures
{
    public static string Read(string fileName)
    {
        return fileName switch
        {
            "upgrade_resourcefield.txt" => UpgradeResourceField,
            "upgrade_building.txt" => UpgradeBuilding,
            "construct_new_building_infrastructure.txt" => InfrastructureChoices,
            "buildingpage_infrastructure.txt" => InfrastructureChoices,
            "buildingpage_military.txt" => MilitaryChoices,
            "buildingpage_resources.txt" => ResourceChoices,
            "TS50_Village - Buildings.txt" => BuildingOverview,
            "daily_quests.txt" => DailyQuests,
            "daily_quests_1.txt" => DailyQuestsAlternative,
            _ => throw new FileNotFoundException($"Could not find DOM fixture '{fileName}'."),
        };
    }

    private const string UpgradeResourceField = """
        <div class="upgradeButtonsContainer">
          <div class="section1">
            <i class="r1Big"></i><span class="value">40</span>
            <i class="r2Big"></i><span class="value">100</span>
            <i class="r3Big"></i><span class="value">50</span>
            <i class="r4Big"></i><span class="value">60</span>
            <div class="duration"><span class="value">00:00:50</span></div>
            <button value="Upgrade to level 1" class="textButtonV1 green build">Upgrade to level 1</button>
          </div>
          <div class="section2">
            <button value="Upgrade to level 1 faster" class="textButtonV1 purple videoFeature">
              Upgrade to level 1 faster
            </button>
          </div>
        </div>
        """;

    private const string UpgradeBuilding = """
        <div class="upgradeButtonsContainer">
          <div class="section1">
            <i class="r1Big"></i><span class="value">90</span>
            <i class="r2Big"></i><span class="value">50</span>
            <i class="r3Big"></i><span class="value">75</span>
            <i class="r4Big"></i><span class="value">25</span>
            <div class="duration"><span class="value">00:08:40</span></div>
            <button value="Upgrade to level 2" class="textButtonV1 green build">Upgrade to level 2</button>
          </div>
          <div class="section2">
            <button value="Upgrade to level 2 faster" class="textButtonV1 purple videoFeature">
              Upgrade to level 2 faster
            </button>
          </div>
        </div>
        """;

    private const string InfrastructureChoices = """
        <div id="contract_building10" data-gid="10">
          <div class="upgradeButtonsContainer"><div class="section1">
            <button value="Construct building" class="textButtonV1 green"
                    onclick="window.location.href='/build.php?id=21&amp;gid=10'">Construct building</button>
          </div></div>
        </div>
        <div id="contract_building23" data-gid="23">
          <div class="upgradeButtonsContainer"><div class="section1">
            <button value="Construct building" class="textButtonV1 green"
                    onclick="window.location.href='/build.php?id=21&amp;gid=23'">Construct building</button>
          </div></div>
        </div>
        """;

    private const string MilitaryChoices = """
        <div id="contract_building19" data-gid="19">
          <div class="upgradeButtonsContainer"><div class="section1">
            <button value="Construct building" class="textButtonV1 green"
                    onclick="window.location.href='/build.php?id=22&amp;gid=19'">Construct building</button>
          </div></div>
        </div>
        """;

    private const string ResourceChoices = """
        <div id="contract_building5" data-gid="5">
          <div class="upgradeButtonsContainer">
            <span class="buildingCondition error">Woodcutter Level 10 required</span>
            <span class="buildingCondition error">Main Building Level 5 required</span>
          </div>
        </div>
        <div id="contract_building6" data-gid="6">
          <div class="upgradeButtonsContainer"><div class="section1">
            <button value="Construct building" class="textButtonV1 green"
                    onclick="window.location.href='/build.php?id=23&amp;gid=6'">Construct building</button>
          </div></div>
        </div>
        """;

    private const string BuildingOverview = """
        <div class="buildingSlot a26 g15" data-name="Main Building" data-level="3">
          <a href="/build.php?id=26&amp;gid=15" data-level="3">
            <div class="labelLayer">Level 3</div>
          </a>
        </div>
        <div class="buildingSlot a31 g19" data-name="Barracks" data-level="2">
          <a href="/build.php?id=31&amp;gid=19" data-level="2">
            <div class="labelLayer">Level 2</div>
          </a>
        </div>
        <div id="sidebar"></div>
        """;

    private const string DailyQuests = """
        <a class="dailyQuests" href="#" accesskey="7">
          <div class="indicator">!</div>
        </a>
        """;

    private const string DailyQuestsAlternative = """
        <a href="/tasks" class="topBarButton dailyQuests active">
          <span>Daily quests</span>
          <div class="active indicator">!</div>
        </a>
        """;
}
