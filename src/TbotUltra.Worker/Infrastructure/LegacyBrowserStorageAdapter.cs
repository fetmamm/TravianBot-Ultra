namespace TbotUltra.Worker.Infrastructure;

internal static class LegacyBrowserStorageAdapter
{
    internal static void MigrateIfNeeded(string legacyPath, string targetPath)
    {
        if (File.Exists(targetPath) || !File.Exists(legacyPath))
        {
            return;
        }

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("Storage state path is invalid.");
        }

        Directory.CreateDirectory(targetDirectory);
        File.Copy(legacyPath, targetPath, overwrite: false);
    }

    internal static void DeleteIfPresent(string legacyPath)
    {
        if (File.Exists(legacyPath))
        {
            File.Delete(legacyPath);
        }
    }
}
