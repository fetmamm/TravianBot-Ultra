using Microsoft.Playwright;
using TbotUltra.Worker.Domain;

namespace TbotUltra.Worker.Services;

public sealed partial class TravianClient
{
    private const string ReportPngBlurStyleId = "tbotReportPngBlur";

    public async Task<ReportPngResult> SaveReportScreenshotAsync(
        string filePath,
        bool hideAttacker,
        bool hideDefender,
        CancellationToken cancellationToken = default)
    {
        Notify("[report-png] save started");
        cancellationToken.ThrowIfCancellationRequested();

        var url = _page.Url ?? string.Empty;
        if (!IsReportUrl(url))
        {
            Notify($"[report-png] skipped: current URL is not a report page. url='{url}'");
            return new ReportPngResult(false, url, null);
        }

        var wrapper = await _page.QuerySelectorAsync("#reportWrapper .role.attacker");
        if (wrapper is null)
        {
            Notify($"[report-png] skipped: report wrapper with attacker role not found. url='{url}'");
            return new ReportPngResult(false, url, null);
        }

        try
        {
            if (hideAttacker || hideDefender)
            {
                Notify($"[report-png] applying blur: attacker={hideAttacker}, defender={hideDefender}");
                await InjectReportPngBlurStyleAsync(hideAttacker, hideDefender);
            }

            Notify($"[report-png] capturing #reportWrapper to '{filePath}'");
            await _page.Locator("#reportWrapper").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = filePath,
            });
            Notify($"[report-png] saved '{filePath}' from url='{url}'");
            return new ReportPngResult(true, url, filePath);
        }
        finally
        {
            if (hideAttacker || hideDefender)
            {
                try
                {
                    await RemoveReportPngBlurStyleAsync();
                }
                catch (Exception ex)
                {
                    Notify($"[report-png] blur cleanup failed: {ex.Message}");
                }
            }
        }
    }

    private static bool IsReportUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.AbsolutePath.StartsWith("/report", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InjectReportPngBlurStyleAsync(bool hideAttacker, bool hideDefender)
    {
        var cssParts = new List<string>();
        if (hideAttacker)
        {
            cssParts.Add(
                "#reportWrapper .role.attacker .troopHeadline span.inline-block," +
                "#reportWrapper .role.attacker .troopHeadline a.player," +
                "#reportWrapper .role.attacker .troopHeadline a.village{filter:blur(7px);}");
        }

        if (hideDefender)
        {
            cssParts.Add(
                "#reportWrapper .role.defender .troopHeadline span.inline-block," +
                "#reportWrapper .role.defender .troopHeadline a.player," +
                "#reportWrapper .role.defender .troopHeadline a.village{filter:blur(7px);}");
        }

        cssParts.Add("#reportWrapper .header .subject{filter:blur(7px);}");
        await _page.EvaluateAsync(
            """
            ({ id, css }) => {
              document.getElementById(id)?.remove();
              const style = document.createElement('style');
              style.id = id;
              style.textContent = css;
              document.head.appendChild(style);
            }
            """,
            new { id = ReportPngBlurStyleId, css = string.Join("\n", cssParts) });
    }

    private Task RemoveReportPngBlurStyleAsync()
    {
        return _page.EvaluateAsync(
            """
            id => {
              document.getElementById(id)?.remove();
            }
            """,
            ReportPngBlurStyleId);
    }
}
