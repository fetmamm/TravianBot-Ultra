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

        if (value.Contains("[loop ") || value.Contains("[tick]") || value.Contains("[loop-pick"))
        {
            return LogCategory.Loop;
        }

        if (value.Contains("[autoq ") || value.Contains("[queue]"))
        {
            return LogCategory.Queue;
        }

        if (Contains(value, "[resources]", "[resources:verbose]", "[transfer]", "[npc", "upgradeallresources", "upgraderesource", "resource read", "resource slot", "resource production", "resource field"))
        {
            return LogCategory.Resources;
        }

        if (Contains(value, "[build]", "[build:verbose]", "[construct]", "[demolish]", "upgradebuilding", "construct", "build queue", "demolish", "buildings snapshot"))
        {
            return LogCategory.Buildings;
        }

        if (Contains(value, "brewery"))
        {
            return LogCategory.Brewery;
        }

        if (Contains(value, "[troops]", "[troops:verbose]", "[reinforce", "[catapult", "troop", "smithy", "build_troops", "barracks", "stable", "workshop", "celebration", "reinforcement"))
        {
            return LogCategory.Troops;
        }

        if (Contains(value, "[hero]", "[hero:verbose]", "hero", "adventure"))
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

        if (Contains(value, "[login]", "[logout]", "[village-switch]", "login", "logged in", "captcha", "verification", "chromium", "session"))
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
            // Diagnostic verbose tags — convention: ":verbose]" suffix anywhere in the prefix
            // means "show only when Clean mode is off". This single rule covers every module's
            // verbose lines: [login:verbose], [village-switch:verbose], [loop-pick:verbose],
            // [build:verbose], [resources:verbose], [hero:verbose], [troops:verbose],
            // [reinforce:verbose], [catapult:verbose], [brewery:verbose], [inbox:verbose], [scan:verbose].
            ":verbose]",
            "building snapshot refreshed",
            "building overview scan looked incomplete",
            "no hero action was needed",
            "[hero_manage started]",
            "[loginasync started]",
            // Catches per-function "[SomeFunctionAsync] started" diagnostics. Note: task-lifecycle
            // lines read "[task STARTED]" (bracket AFTER the word) so they are NOT matched here.
            "] started",
            "evaluating slot=",
            "clicking upgrade",
            "click result",
            "[population]",
            "start interval=",
            " wait ",
            " deferred]",
            " defer ",
            " deferred ",
            "queue_wait_seconds=",
            "next try in",
            "build queue full",
            "resource production update",
            "readcurrentpageresourceproductionperhourasync",
            "[upgradeallresourcestolevelasync]",
            "actionability=",
            "already meets",
            "not actionable",
            "smart order by stock",
            "smart ordered by",
            "tracked stocks equal",
            "resource-production",
            "resource read:",
            "storage read:",
            "quick resource status",
            "quick storage status",
            "readactiveconstructionsasync",
            "evaluateconstructionslotsasync",
            "ensuring logged in",
            "checkloggedinasync",
            "adventures on current page",
            "[herohome",
            // Note: bare "loginasync" pattern intentionally removed — [login] milestones now
            // use the "[login] ..." prefix which is NOT verbose. Per-function "[LoginAsync] started"
            // is no longer emitted (replaced with explicit account/server context).
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
            "logged in confirmed",
            "ui sync snapshot",
            "current resource production for ui",
            "preliminary target",
            // Browser/session/navigation plumbing — high-volume, not actionable in Clean mode.
            "[browser-op",
            "[browser-session",
            "[browser]",
            "[nav]",
            "[ensure-logged-in",
            "[flavor]",
            "[pacing]",
            // High-volume lifecycle/bookkeeping tags the user doesn't need in Clean mode.
            "[queue]",
            "[login]",
            "[tick]",
            "[village-switch",
            "[storage-refresh",
            "[handleresourcesnapshotrefreshtickasync]");
    }

    public static bool IsExpectedFarmListResult(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var value = message.ToLowerInvariant();
        return (value.StartsWith("finished '", StringComparison.Ordinal)
                && value.Contains("added=", StringComparison.Ordinal)
                && value.Contains("duplicates=", StringComparison.Ordinal)
                && value.Contains("invalid=", StringComparison.Ordinal)
                && value.Contains("failed=", StringComparison.Ordinal))
            || (value.Contains("removed ", StringComparison.Ordinal)
                && value.Contains(" invalid coordinate(s)", StringComparison.Ordinal)
                && value.Contains("travco list", StringComparison.Ordinal))
            || (value.StartsWith("[travco] removed ", StringComparison.Ordinal)
                && value.Contains(" invalid coordinate(s)", StringComparison.Ordinal));
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
