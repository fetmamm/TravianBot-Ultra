using System.IO;
using TbotUltra.Core.Accounts;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// One-time migration of the legacy global queue file (<c>config/queue.json</c>) to the per-account
/// queue file (<c>config/accounts/&lt;account&gt;/queue.json</c>). The queue used to be shared by every
/// account; scoping it per account means the old global file must be moved to the account that is
/// active on first run after the upgrade. The legacy file is kept as <c>queue.json.bak</c> so nothing
/// is lost if the move is unexpected.
/// </summary>
public static class QueueMigration
{
    public static void MigrateLegacyGlobalQueue(string projectRoot, string accountName, Action<string>? log = null)
    {
        try
        {
            var legacyPath = AccountStoragePaths.LegacyGlobalQueuePath(projectRoot);
            if (!File.Exists(legacyPath))
            {
                return;
            }

            var targetPath = AccountStoragePaths.AccountQueuePath(projectRoot, accountName);
            if (File.Exists(targetPath))
            {
                // The active account already has its own queue; don't clobber it. Just retire the
                // legacy file so this migration does not run again.
                BackupLegacyFile(legacyPath, log);
                return;
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(legacyPath, targetPath, overwrite: false);
            BackupLegacyFile(legacyPath, log);
            log?.Invoke($"Migrated legacy queue to per-account file for '{accountName}'.");
        }
        catch (Exception ex)
        {
            // A failed migration must never block startup; the worst case is an empty per-account queue.
            log?.Invoke($"Queue migration skipped: {ex.Message}");
        }
    }

    private static void BackupLegacyFile(string legacyPath, Action<string>? log)
    {
        try
        {
            var backupPath = $"{legacyPath}.bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(legacyPath, backupPath);

            // The legacy file had its own lock sidecar; remove it so no stale lock lingers.
            var legacyLock = $"{legacyPath}.lock";
            if (File.Exists(legacyLock))
            {
                File.Delete(legacyLock);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not back up legacy queue file: {ex.Message}");
        }
    }
}
