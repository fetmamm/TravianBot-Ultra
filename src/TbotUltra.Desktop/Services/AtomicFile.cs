namespace TbotUltra.Desktop.Services;

// Compatibility entry point for existing Desktop callers. The implementation lives in Core so
// Worker persistence follows the same atomic write and transient-lock retry policy.
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
        => TbotUltra.Core.Infrastructure.AtomicFile.WriteAllText(path, content);
}
