namespace TbotUltra.Worker.Domain;

public sealed class ManualVerificationRequiredException : InvalidOperationException
{
    public ManualVerificationRequiredException(string message) : base(message)
    {
    }
}
