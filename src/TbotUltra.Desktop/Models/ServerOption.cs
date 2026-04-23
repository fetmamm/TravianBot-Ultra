namespace TbotUltra.Desktop.Models;

public sealed class ServerOption
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;

    public override string ToString()
    {
        return Name;
    }
}
