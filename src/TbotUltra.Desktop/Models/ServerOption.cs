namespace TbotUltra.Desktop.Models;

public sealed class ServerOption
{
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;

    public override string ToString()
    {
        return Name;
    }
}
