namespace TbotUltra.Worker.Configuration;

public sealed class AccountOptions
{
    public required string Name { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public string ServerUrl { get; init; } = string.Empty;

    // Per-account proxy. When enabled, the account's browser traffic is routed through ProxyServer so
    // different accounts can present different egress IPs. OFF by default.
    public bool ProxyEnabled { get; init; }
    public string ProxyServer { get; init; } = string.Empty;
}
