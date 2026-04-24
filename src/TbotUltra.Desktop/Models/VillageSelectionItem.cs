namespace TbotUltra.Desktop.Models;

public sealed class VillageSelectionItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool IsCapital { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    }
}
