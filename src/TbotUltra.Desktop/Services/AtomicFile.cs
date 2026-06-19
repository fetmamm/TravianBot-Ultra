using System;
using System.IO;
using System.Threading;

namespace TbotUltra.Desktop.Services;

/// <summary>
/// Crash-safe text file writer. The content is first written to a unique temp file in the same
/// directory and then swapped in with <see cref="File.Move(string, string, bool)"/>, which is an
/// atomic rename on the same volume. A crash, power loss or process kill mid-write therefore leaves
/// the original file fully intact (or absent) — never half-written/corrupt JSON.
///
/// This mirrors the temp-file + move pattern already used by the queue store
/// (<c>JsonQueueStore.SaveMutable</c>) and is the single place the account/village settings and cache
/// stores route their writes through, so config corruption can no longer split an account's state.
///
/// A small retry covers transient sharing violations (antivirus/indexer briefly holding the file).
/// Callers that need in-process serialization still hold their own lock around this call.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must be provided.", nameof(path));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Unique temp name so two writers targeting the same file (different locks/paths) never collide
        // on the temp file itself; the final move is what serializes the visible result.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            RetryFileIo(() =>
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, path, overwrite: true);
                return true;
            });
        }
        finally
        {
            // If the move succeeded the temp file is already gone; this only cleans up a temp left
            // behind by a failed/retried attempt so temp files do not accumulate.
            TryDeleteTemp(tempPath);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup; a stale temp file is harmless and will be overwritten next time.
        }
    }

    private static T RetryFileIo<T>(Func<T> action)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
