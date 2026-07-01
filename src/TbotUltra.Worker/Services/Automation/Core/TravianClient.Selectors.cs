namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private static class Paths
    {
        public const string Resources = "/dorf1.php";
        public const string Buildings = "/dorf2.php";
        public const string PlayerProfile = "/spieler.php";
        public const string Statistics100 = "/statistiken.php?id=100";
        // Questmaster task overview.
        public const string Tasks = "/tasks";

        public const string FarmListFastUp = "/build.php?id=39&fastUP=0";

        public static string BuildBySlot(int slotId) =>
            $"/build.php?id={slotId}";

        public static string FarmListBySlotId(string lid) =>
            $"/build.php?id=39&gid=16&tt=99&action=showSlot&lid={lid}";

        public static readonly IReadOnlyList<string> LogoutCandidates = new[]
        {
            "/logout.php",
            "/?action=logout",
            "/index.php?logout=1",
        };
    }

    private string RallyPointTroopsPath =>
        "/build.php?id=39&gid=16&tt=1";

    private string RallyPointSendTroopsPath =>
        "/build.php?id=39&gid=16&tt=2";

    private string RallyPointFarmListPath =>
        "/build.php?id=39&gid=16&tt=99";

    private string HeroAdventuresPath =>
        "/hero/adventures";

    private string HeroInventoryPath =>
        "/hero/inventory";

    private string HeroAttributesPath =>
        "/hero/attributes";

    private string MessagesPath =>
        "/messages";

    private string MessagesWritePath =>
        "/messages/write";

    private string ReportsPath =>
        "/report";

    private static class Selectors
    {
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
            "button[name*='logout' i]",
            "input[name*='logout' i]",
            "form[action*='logout'] button[type='submit']",
            "form[action*='logout'] input[type='submit']",
            "a:has-text('Logout')",
            "a:has-text('Log out')",
            "button:has-text('Logout')",
            "button:has-text('Log out')",
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
            "a[href*='logout']",
            "img[alt*='Logout' i]",
            "a[href*='dorf1.php']",
            "a[href*='dorf2.php']",
            "#sidebarBoxVillagelist",
            ".villageList",
            "#villageList",
            "#resourceFieldContainer",
            "#village_map",
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
