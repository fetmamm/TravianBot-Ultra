using System.IO.Compression;
using System.IO;
using System.Text;

namespace TbotUltra.Desktop.Services;

internal sealed class DiagnosticsExporter
{
    private static readonly HashSet<string> GlobalConfigurationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "bot.json",
        "settings.json",
        "servers.user.json",
        "proxies.json",
        "building_templates.json",
    };

    private static readonly HashSet<string> AccountConfigurationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.json",
        "proxy_plan.json",
        "proxy_runtime.json",
        "proxy_usage.json",
        "queue.json",
    };

    internal Task<DiagnosticsExportResult> CreateAsync(DiagnosticsExportRequest request, CancellationToken cancellationToken = default)
        => Task.Run(() => Create(request, cancellationToken), cancellationToken);

    private static DiagnosticsExportResult Create(DiagnosticsExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        CreateDirectoryWithRetry(request.OutputDirectory);
        var stamp = request.GeneratedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmssfff");
        var stagingPath = Path.Combine(request.OutputDirectory, $".staging-{Guid.NewGuid():N}");
        var temporaryZipPath = Path.Combine(request.OutputDirectory, $".diagnostics-{Guid.NewGuid():N}.tmp");
        var zipPath = FindAvailableZipPath(request.OutputDirectory, $"TbotUltra-diagnostics-{stamp}");
        var included = new List<string>();
        var missing = new List<string>();

        try
        {
            CreateDirectoryWithRetry(stagingPath);
            CopyLogs(request, stagingPath, included, missing, cancellationToken);
            WriteTerminal(request.TerminalEntries, stagingPath, included);
            WriteSanitizedConfiguration(request.ProjectRoot, stagingPath, included, missing, cancellationToken);
            if (request.IncludeRuntimeDiagnostics)
            {
                CopyRuntimeDiagnostics(request.ProjectRoot, stagingPath, included, missing, cancellationToken);
            }

            WriteManifest(request, stagingPath, included, missing);
            RetryFileIo(() =>
            {
                TryDeleteFile(temporaryZipPath);
                ZipFile.CreateFromDirectory(stagingPath, temporaryZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            });
            RetryFileIo(() => File.Move(temporaryZipPath, zipPath));
            return new DiagnosticsExportResult(zipPath, included.AsReadOnly(), missing.AsReadOnly());
        }
        finally
        {
            TryDeleteFile(temporaryZipPath);
            TryDeleteDirectory(stagingPath);
        }
    }

    private static void CopyLogs(
        DiagnosticsExportRequest request,
        string stagingPath,
        List<string> included,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        var logDirectories = new[]
        {
            Path.Combine(request.ProjectRoot, "logs"),
            Path.Combine(request.AppBaseDirectory, "logs"),
        }
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in logDirectories)
        {
            if (!Directory.Exists(directory))
            {
                missing.Add($"Log directory not found: {SanitizePath(directory)}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
            {
                sourceFiles.Add(Path.GetFullPath(file));
            }
        }

        if (sourceFiles.Count == 0)
        {
            missing.Add("No .txt or .log files were found in the log directories.");
            return;
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveName = MakeUniqueFileName(Path.GetFileName(sourcePath), usedNames);
            var relativePath = Path.Combine("logs", archiveName);
            WriteSanitizedSharedTextFile(sourcePath, Path.Combine(stagingPath, relativePath));
            included.Add(ToArchivePath(relativePath));
        }
    }

    private static void WriteTerminal(IReadOnlyList<string> terminalEntries, string stagingPath, List<string> included)
    {
        var relativePath = "current-terminal.txt";
        var content = string.Join(Environment.NewLine, terminalEntries.Select(DiagnosticsSanitizer.SanitizeText));
        WriteAllTextWithRetry(Path.Combine(stagingPath, relativePath), content);
        included.Add(relativePath);
    }

    private static void WriteSanitizedConfiguration(
        string projectRoot,
        string stagingPath,
        List<string> included,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        var configRoot = Path.Combine(projectRoot, "config");
        if (!Directory.Exists(configRoot))
        {
            missing.Add("Configuration directory not found.");
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(configRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => GlobalConfigurationFiles.Contains(Path.GetFileName(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteSanitizedConfigurationFile(sourcePath, Path.Combine("configuration", "global", Path.GetFileName(sourcePath)), stagingPath, included);
        }

        var accountsRoot = Path.Combine(configRoot, "accounts");
        if (!Directory.Exists(accountsRoot))
        {
            missing.Add("Account configuration directory not found.");
            return;
        }

        var accountIndex = 0;
        foreach (var accountDirectory in Directory.EnumerateDirectories(accountsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            accountIndex++;
            var safeAccountName = $"account-{accountIndex:D3}";
            foreach (var sourcePath in Directory.EnumerateFiles(accountDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .Where(path => AccountConfigurationFiles.Contains(Path.GetFileName(path))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteSanitizedConfigurationFile(
                    sourcePath,
                    Path.Combine("configuration", "accounts", safeAccountName, Path.GetFileName(sourcePath)),
                    stagingPath,
                    included);
            }
        }
    }

    private static void WriteSanitizedConfigurationFile(string sourcePath, string relativePath, string stagingPath, List<string> included)
    {
        var content = ReadSharedTextWithRetry(sourcePath);
        var sanitized = DiagnosticsSanitizer.SanitizeJson(content);
        var destinationPath = Path.Combine(stagingPath, relativePath);
        CreateDirectoryWithRetry(Path.GetDirectoryName(destinationPath)!);
        WriteAllTextWithRetry(destinationPath, sanitized);
        included.Add(ToArchivePath(relativePath));
    }

    private static void CopyRuntimeDiagnostics(
        string projectRoot,
        string stagingPath,
        List<string> included,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(projectRoot, "temp_build_out", "diagnostics");
        if (!Directory.Exists(sourceRoot))
        {
            missing.Add("Runtime diagnostics directory not found.");
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRelativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var relativePath = Path.Combine("runtime-diagnostics", sourceRelativePath);
            var destinationPath = Path.Combine(stagingPath, relativePath);
            CreateDirectoryWithRetry(Path.GetDirectoryName(destinationPath)!);

            if (sourcePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || sourcePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                || sourcePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || sourcePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                WriteSanitizedSharedTextFile(sourcePath, destinationPath);
            }
            else
            {
                CopySharedFileWithRetry(sourcePath, destinationPath);
            }

            included.Add(ToArchivePath(relativePath));
        }
    }

    private static void WriteManifest(
        DiagnosticsExportRequest request,
        string stagingPath,
        IReadOnlyList<string> included,
        IReadOnlyList<string> missing)
    {
        var lines = new List<string>
        {
            "=== Tbot Ultra Diagnostics ===",
            "This package was sanitized automatically. Runtime screenshots may still contain visible game data.",
            string.Empty,
        };
        lines.AddRange(SystemDiagnosticsInfo.BuildLines(request.AppVersion, request.ProjectRoot, request.GeneratedUtc)
            .Select(DiagnosticsSanitizer.SanitizeText));
        lines.Add(string.Empty);
        lines.Add("=== INCLUDED FILES ===");
        lines.AddRange(included.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        lines.Add("diagnostics.txt");
        lines.Add(string.Empty);
        lines.Add("=== OPTIONAL FILES / NOTES ===");
        lines.AddRange(missing.Count == 0 ? ["None."] : missing.Select(DiagnosticsSanitizer.SanitizeText));

        RetryFileIo(() => File.WriteAllLines(
            Path.Combine(stagingPath, "diagnostics.txt"),
            lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
    }

    private static void WriteSanitizedSharedTextFile(string sourcePath, string destinationPath)
    {
        var content = ReadSharedTextWithRetry(sourcePath);
        CreateDirectoryWithRetry(Path.GetDirectoryName(destinationPath)!);
        WriteAllTextWithRetry(destinationPath, DiagnosticsSanitizer.SanitizeText(content));
    }

    private static string ReadSharedTextWithRetry(string path)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < 3)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        throw new IOException($"Could not read diagnostics source file '{Path.GetFileName(path)}' after 3 attempts.", lastError);
    }

    private static void CopySharedFileWithRetry(string sourcePath, string destinationPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                TryDeleteFile(destinationPath);
                if (attempt < 3)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        throw new IOException($"Could not copy diagnostics source file '{Path.GetFileName(sourcePath)}' after 3 attempts.", lastError);
    }

    private static string MakeUniqueFileName(string fileName, ISet<string> usedNames)
    {
        if (usedNames.Add(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{stem}-{suffix}{extension}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string FindAvailableZipPath(string directory, string baseName)
    {
        for (var suffix = 0; ; suffix++)
        {
            var fileName = suffix == 0 ? $"{baseName}.zip" : $"{baseName}-{suffix + 1}.zip";
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private static string SanitizePath(string path) => DiagnosticsSanitizer.SanitizeText(path);

    private static string ToArchivePath(string path) => path.Replace('\\', '/');

    private static void CreateDirectoryWithRetry(string path)
        => RetryFileIo(() => Directory.CreateDirectory(path));

    private static void WriteAllTextWithRetry(string path, string content)
        => RetryFileIo(() => File.WriteAllText(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));

    private static void RetryFileIo(Action action)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < 4)
                {
                    Thread.Sleep(75 * attempt);
                }
            }
        }

        throw lastError ?? new IOException("Diagnostics file operation failed.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed record DiagnosticsExportRequest(
    string ProjectRoot,
    string AppBaseDirectory,
    string OutputDirectory,
    string AppVersion,
    IReadOnlyList<string> TerminalEntries,
    DateTimeOffset GeneratedUtc,
    bool IncludeRuntimeDiagnostics = true);

internal sealed record DiagnosticsExportResult(
    string ZipPath,
    IReadOnlyList<string> IncludedFiles,
    IReadOnlyList<string> MissingFiles);
