namespace TbotUltra.Core.Tasks;

public sealed record TaskDescriptor(
    string Name,
    TaskGroup Group,
    string DisplayName,
    bool IsRuntimeAllowed,
    TaskPayloadKind PayloadKind);
