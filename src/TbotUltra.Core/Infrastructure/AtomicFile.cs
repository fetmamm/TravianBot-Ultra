namespace TbotUltra.Core.Infrastructure;

public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(temporaryPath, content);
                    File.Move(temporaryPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8)
                {
                    Thread.Sleep(Math.Min(1_000, 50 * (1 << Math.Min(attempt - 1, 5))));
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A stale temporary file is harmless and is cleaned by the next write.
            }
        }
    }
}
