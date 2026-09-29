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
}
