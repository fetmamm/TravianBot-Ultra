using TbotUltra.Worker.Configuration;

namespace TbotUltra.Worker.Services;

public interface IAccountProvider
{
    AccountOptions LoadAccount(string? accountName = null);
}
