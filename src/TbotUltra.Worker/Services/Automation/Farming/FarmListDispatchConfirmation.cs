namespace TbotUltra.Worker.Services;

internal readonly record struct FarmListDispatchConfirmation(bool HasSuccess, bool HasError)
{
    internal bool IsConfirmed => HasSuccess || HasError;

    internal string Description => (HasSuccess, HasError) switch
    {
        (true, true) => "success+error",
        (true, false) => "success",
        (false, true) => "error",
        _ => "none",
    };

    internal string LogDescription => (HasSuccess, HasError) switch
    {
        (true, true) => "processed; some targets were skipped",
        (true, false) => "confirmed",
        (false, true) => "processed",
        _ => "unconfirmed",
    };
}
