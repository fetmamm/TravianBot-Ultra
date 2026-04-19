namespace TbotUltra.Worker.Configuration;

public sealed class AccountOptions
{
    public required string Name { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public string ServerUrl { get; init; } = string.Empty;
}
