using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

/// <summary>
/// Reads the daily server-reset hour from the Daily Quests dialog. The dialog renders a line like
/// "(Next reset at 13:00. Make sure to collect your reward before!)" — the hour there is the server-local
/// whole hour when the day's quests/rewards reset (and, in practice, when the free +15% video re-enables).
/// Also builds/parses the compact result token the worker hands back to the desktop.
/// </summary>
public static class DailyResetDomParser
{
    public const string TokenPrefix = "daily_reset_hour=";

    private static readonly Regex ResetTextRegex = new(
        @"Next reset at\s*(?<hour>\d{1,2}):(?<minute>\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TokenRegex = new(
        @"daily_reset_hour=(?<hour>\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Server-local whole hour (0..23) parsed from the dialog text, or null when the reset line is absent
    /// or malformed. Minutes are ignored — the reset always lands on a whole hour and callers only need it
    /// approximately (a random human delay is layered on top).
    /// </summary>
    public static int? TryParseResetHourFromDialogHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = ResetTextRegex.Match(WebUtility.HtmlDecode(html));
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["hour"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
            || hour is < 0 or > 23)
        {
            return null;
        }

        return hour;
    }

    public static string BuildResetHourToken(int hour) => $"{TokenPrefix}{Math.Clamp(hour, 0, 23)}";

    public static int? TryParseResetHourToken(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = TokenRegex.Match(message);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["hour"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)
               && hour is >= 0 and <= 23
            ? hour
            : null;
    }
}
