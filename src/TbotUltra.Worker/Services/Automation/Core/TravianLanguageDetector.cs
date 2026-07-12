using System.Text.RegularExpressions;

namespace TbotUltra.Worker.Services;

internal static partial class TravianLanguageDetector
{
    public const string ExpectedLanguage = "en-US";

    public static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsExpected(string? language)
        => string.Equals(Normalize(language), ExpectedLanguage, StringComparison.OrdinalIgnoreCase);

    public static string? ExtractFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (BrowserErrorDocumentRegex().IsMatch(html))
        {
            return null;
        }

        var gameLanguage = ExtractGameLanguage(html);
        if (!string.IsNullOrWhiteSpace(gameLanguage))
        {
            return gameLanguage;
        }

        var bodyLanguage = ExtractAttribute(html, "body", "data-language");
        if (!string.IsNullOrWhiteSpace(bodyLanguage))
        {
            return bodyLanguage;
        }

        return ExtractAttribute(html, "html", "lang");
    }

    private static string? ExtractGameLanguage(string html)
    {
        var match = TravianGameLanguageRegex().Match(html);
        return match.Success ? Normalize(match.Groups["value"].Value) : null;
    }

    private static string? ExtractAttribute(string html, string tagName, string attributeName)
    {
        var tagMatch = Regex.Match(
            html,
            $@"<{Regex.Escape(tagName)}\b(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!tagMatch.Success)
        {
            return null;
        }

        var attrMatch = Regex.Match(
            tagMatch.Groups["attrs"].Value,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*([""'])(?<value>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return attrMatch.Success ? Normalize(attrMatch.Groups["value"].Value) : null;
    }

    [GeneratedRegex(@"Travian\.Game\.language\s*=\s*([""'])(?<value>.*?)\1", RegexOptions.IgnoreCase)]
    private static partial Regex TravianGameLanguageRegex();

    [GeneratedRegex(@"<body\b[^>]*\bclass\s*=\s*([""'])[^""']*\bneterror\b|id\s*=\s*([""'])main-frame-error\2|ERR_[A-Z_]+", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BrowserErrorDocumentRegex();
}
