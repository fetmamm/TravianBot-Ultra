namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    internal static class Paths
    {
        public const string Resources = "/dorf1.php";
        public const string Login = "/login.php";
        public const string LobbyAccount = "https://lobby.legends.travian.com/account";
        public const string Buildings = "/dorf2.php";
        public const string Options = "/options";
        public const string PlayerProfile = "/spieler.php";
        public const string Map = "/karte.php";
        // Questmaster task overview.
        public const string Tasks = "/tasks";
        public const string RallyPointTroops = "/build.php?id=39&gid=16&tt=1";
        public const string RallyPointSendTroops = "/build.php?id=39&gid=16&tt=2";
        public const string RallyPointFarmLists = "/build.php?id=39&gid=16&tt=99";
        public const string HeroAdventures = "/hero/adventures";
        public const string HeroInventory = "/hero/inventory";
        public const string HeroAttributes = "/hero/attributes";
        public const string Messages = "/messages";
        public const string MessagesWrite = "/messages/write";
        public const string Reports = "/report";

        public const string FarmListFastUp = "/build.php?id=39&fastUP=0";

        public static string BuildBySlot(int slotId) =>
            $"/build.php?id={slotId}";

        public static string BuildBySlotTab(int slotId, int tab) =>
            $"/build.php?id={slotId}&t={tab}";

        public static string BuildBySlotWithGid(int slotId, int? gid)
        {
            var path = BuildBySlot(slotId);
            if (gid is int value && value > 0)
            {
                path += $"&gid={value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            return path;
        }

        public static string BuildBySlotWithCategory(int slotId, int categoryIndex) =>
            $"{BuildBySlot(slotId)}&category={categoryIndex}";

        public static string FarmListBySlotId(string lid) =>
            $"/build.php?id=39&gid=16&tt=99&action=showSlot&lid={lid}";

        public static readonly IReadOnlyList<string> LogoutCandidates = new[]
        {
            "/logout",
        };
    }

    private static class Selectors
    {
        public const string LobbyGameWorldCard = "div.gameworld[data-wuid]";
        public const string LobbyGameWorldName = ".gameworldName";
        public const string LobbyPlayNowButton = "button.playNow";
        public const string CurrentPlayerName = ".content > .playerName";
        public const string CurrentAllianceName = "#sidebarBoxAlliance .content .boxTitle .name";

        public static readonly string[] LoginUsernameField =
        {
            "input[name='name']",
            "input[name='username']",
            "input[name='user']",
            "input[name='login']",
            "input[type='email']",
            "input[type='text']",
        };

        public static readonly string[] LoginPasswordField =
        {
            "input[type='password']",
            "input[name='password']",
        };

        public static readonly string[] CaptchaInputField =
        {
            "input[name='captcha_answer']",
            "input[id='captcha_answer']",
            "input[type='number'].captcha-input",
            "input[placeholder='Answer']",
            "input[id*='CaptchaAnswer' i]",
            "input[id*='captchaanswer' i]",
            "input[name='captcha' i]",
            "input[id*='captcha' i]",
            "input[placeholder*='captcha' i]",
            "input[name*='verification' i]",
            "input[id*='verification' i]",
            "input[name*='answer' i]",
            "input[id*='answer' i]",
        };

        // Strict subset used for automatic account holds. The broader CaptchaInputField list is
        // useful for the interactive captcha flow, but generic "answer" inputs are not strong
        // enough evidence to stop an account.
        public static readonly string[] AccountChallengeInputField =
        {
            "input[name='captcha_answer']",
            "input[id='captcha_answer']",
            "input[type='number'].captcha-input",
            "input[id*='captcha' i]",
            "input[name*='captcha' i]",
            "input[name*='verification' i]",
            "input[id*='verification' i]",
        };

        // Official punishment page shown after a blocked game action. These controls are evidence only;
        // automation must never click either the punishment acceptance or support action.
        public const string BanPunishmentControls =
            "#content.universal #sysmsg #punishmentMsgButtons button.continueButton, "
            + "#content.universal #sysmsg #punishmentMsgButtons button.contactSupportButton";

        // Official sidebar infobox shown while an avatar remains banned. Require the warning and its
        // dedicated details link together so unrelated infoType_2 warnings cannot stop automation.
        public const string BanSidebarInfo =
            "#sidebarBoxInfobox li.infoType_2:has(span.warning):has(a[href='/dorf1.php?action=stop'])";

        public static readonly string[] LoginButton =
        {
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('Login')",
            "button:has-text('Log in')",
            "a:has-text('Login')",
        };

        public static readonly string[] LogoutTriggers =
        {
            // Official T4.6: the logout control is an <a> with no href and only an SVG icon — it fires
            // Travian.api('auth/logout') via onclick. Match it by the onclick/class first.
            "a[onclick*='auth/logout']",
            "a.layoutButton.logout",
            "a.logout[onclick]",
            "a[href*='logout']",
            "a:has-text('Logout')",
            "a:has-text('Log out')",
        };

        public static readonly string[] CaptchaSubmitButton =
        {
            "form:has(input[name*='captcha' i]) button[type='submit']",
            "form:has(input[id*='captcha' i]) button[type='submit']",
            "form:has(input[name*='captcha' i]) input[type='submit']",
            "form:has(input[id*='captcha' i]) input[type='submit']",
            "div.button-container:has(.text:text-is('OK'))",
            "div.button-content:has(.text:text-is('OK'))",
            "div.addHoverClick:has(.text:text-is('OK'))",
            "button[type='submit']",
            "input[type='submit']",
            "button:has-text('OK')",
            "div:has-text('OK')",
            "button:has-text('Submit')",
            "button:has-text('Verify')",
            "button:has-text('Continue')",
            "button:has-text('Login')",
            "button:has-text('Log in')",
        };

        public static readonly string[] CaptchaErrorDialogOkButton =
        {
            "button.dialogButtonOk",
            ".dialog-contents button.green.ok",
            ".dialog-contents .button-container:has(.text:text-is('OK'))",
            ".dialog-contents .button-content:has(.text:text-is('OK'))",
        };

        public static readonly string[] CaptchaSuccessDialogOkButton =
        {
            "button.green.ok.dialogButtonOk[type='submit']",
            "button.dialogButtonOk",
            ".dialog-contents button.green.ok",
            ".dialog-contents .button-container:has(.text:text-is('OK'))",
            ".dialog-contents .button-content:has(.text:text-is('OK'))",
        };

        public static readonly string[] LoggedInIndicators =
        {
            // Stable Official in-game shell markers. These are rendered on authenticated pages beyond
            // Dorf1/Dorf2 as well, so login detection can stay on the user's current page.
            "#heroImageButton[href^='/hero']",
            "img.heroImage[alt='Hero']",
            "#sidebarBoxActiveVillage",
            "#villageName[data-x][data-y]",
            "#sidebarBoxVillageList",
            "a[href*='logout']",
            "img[alt*='Logout' i]",
            "a[href*='dorf1.php']",
            "a[href*='dorf2.php']",
            ".villageList",
            "#villageList",
            "#resourceFieldContainer",
            "#village_map",
            ContinueAfterUpdateLink,
        };

        public static readonly string[] LoggedOutIndicators =
        {
            // Official T4.6 renders a React login scene (body.login / #loginScene) instead of redirecting
            // to login.php — keep these first so sign-out is confirmed positively on official too.
            "#loginScene",
            "body.login",
            "input[type='password']",
            "input[name='password']",
            "button[type='submit']",
            "input[type='submit']",
            "a[href*='login']",
        };

        public const string ContinueAfterUpdateLink = "a[href*='dorf1.php?ok']";
    }
}
