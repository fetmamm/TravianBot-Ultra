namespace TbotUltra.Worker.Domain;

public sealed class UnexpectedTravianLanguageException : InvalidOperationException
{
    public UnexpectedTravianLanguageException(string? currentLanguage)
        : base(BuildMessage(currentLanguage))
    {
        CurrentLanguage = currentLanguage;
    }

    public string? CurrentLanguage { get; }

    private static string BuildMessage(string? currentLanguage)
    {
        var display = string.IsNullOrWhiteSpace(currentLanguage) ? "unknown" : currentLanguage.Trim();
        return $"Travian language must be English (en-US), but current language is '{display}'.";
    }
}
