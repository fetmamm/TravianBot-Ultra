using System;

namespace TbotUltra.Desktop.Services.Logging;

/// <summary>Coarse category used to filter the in-app log into per-task views.</summary>
public enum LogCategory
{
    All = 0,
    Loop,
    Queue,
    Resources,
    Buildings,
    Hero,
    Farming,
    Troops,
    Brewery,
    Inbox,
    Login,
    Errors,
    Other,
}

/// <summary>
/// Pure string-based classification of log lines. The desktop log has no structured
/// severity/category metadata, so we derive both from the message text (same approach as the
/// existing IsAlarmMessage heuristic). Used to back the Logs-tab category filter and "Clean" mode.
/// </summary>
public static class LogClassifier
{
    /// <summary>User-facing labels for the category filter dropdown (in display order).</summary>
    public static readonly (LogCategory Category, string Label)[] FilterOptions =
    [
        (LogCategory.All, "All"),
        (LogCategory.Loop, "Loop"),
        (LogCategory.Queue, "Queue"),
        (LogCategory.Resources, "Resources"),
        (LogCategory.Buildings, "Buildings"),
        (LogCategory.Hero, "Hero"),
        (LogCategory.Farming, "Farming"),
        (LogCategory.Troops, "Troops"),
        (LogCategory.Brewery, "Brewery"),
        (LogCategory.Inbox, "Inbox"),
        (LogCategory.Login, "Login"),
        (LogCategory.Errors, "Errors"),
        (LogCategory.Other, "Other"),
    ];

    public static LogCategory Classify(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return LogCategory.Other;
        }

        var value = message.ToLowerInvariant();

        if (value.Contains("[loop "))
        {
            return LogCategory.Loop;
        }

        if (value.Contains("[autoq "))
        {
            return LogCategory.Queue;
        }

        if (Contains(value, "upgradeallresources", "upgraderesource", "resource read", "resource slot", "resource production", "resource field"))
        {
            return LogCategory.Resources;
        }

        if (Contains(value, "upgradebuilding", "construct", "build queue", "demolish", "buildings snapshot"))
        {
            return LogCategory.Buildings;
        }

        if (Contains(value, "brewery"))
        {
            return LogCategory.Brewery;
        }

        if (Contains(value, "troop", "smithy", "build_troops", "barracks", "stable", "workshop", "celebration", "reinforcement"))
        {
            return LogCategory.Troops;
        }

        if (Contains(value, "hero", "adventure"))
        {
            return LogCategory.Hero;
        }

        if (Contains(value, "farm", "raid", "natar"))
        {
            return LogCategory.Farming;
        }

        if (Contains(value, "inbox", "message", "report", "readinboxstatus", "markmessages", "markreports"))
        {
            return LogCategory.Inbox;
        }

        if (Contains(value, "login", "logged in", "captcha", "verification", "chromium", "session"))
        {
            return LogCategory.Login;
        }

        return LogCategory.Other;
    }

    /// <summary>
    /// Blacklist of high-volume noise lines hidden in "Clean" mode. Deliberately a blacklist
    /// (not a whitelist) so milestone/important lines are never accidentally hidden.
    /// </summary>
    public static bool IsVerbose(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();

        return Contains(
            value,
            "starting tick",
            "building snapshot refreshed",
            "[tryreadactivevillagenamesafeasync] attempting to read active village name from page",
            "target village applied:",
            "building overview scan looked incomplete",
            "] started",
            " scanned ",
            "evaluating slot=",
            "clicking upgrade",
            "actionability=",
            "already meets",
            "not actionable",
            "smart order by stock",
            "smart ordered by",
            "tracked stocks equal",
            "resource-production",
            "resource read:",
            "readactiveconstructionsasync",
            "evaluateconstructionslotsasync",
            "readinboxstatus",
            "ensuring logged in",
            "checkloggedinasync",
            "loginasync",
            "refreshaccountfeaturesignalsasync",
            "istravianplusactiveasync",
            "goldclub",
            "gold club",
            "tribe",
            "resource refresh",
            "resource-refresh",
            "resource snapshot",
            "transient navigation",
            "ui sync",
            "ui-sync",
            "[resource-ui]",
            "resource status",
            "[plus]",
            "you are logged in",
            "logged in confirmed",
            "ui sync snapshot",
            "current resource production for ui",
            "preliminary target");
    }

    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
