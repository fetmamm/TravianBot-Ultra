namespace TbotUltra.Desktop.Models;

public sealed class VillageSelectionItem
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public override string ToString()
    {
        return Name;
    }
}
