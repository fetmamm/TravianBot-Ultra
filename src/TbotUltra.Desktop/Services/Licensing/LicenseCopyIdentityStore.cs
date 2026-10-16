using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Services.Licensing;

public sealed record LicenseCopyIdentity(Guid CopyId, string MachineFingerprintHash);

/// <summary>One stable copy ID per portable installation folder; the machine hash limits trial reuse.</summary>
public static class LicenseCopyIdentityStore
{
    public static LicenseCopyIdentity LoadOrCreate(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "config", "license-copy.json");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machineGuid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            throw new InvalidOperationException("Windows machine identity is unavailable for licensing.");
        }

        var normalizedRoot = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var installationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"TbotUltra/copy/v1/{machineGuid}/{normalizedRoot}")))
            .ToLowerInvariant();
        Guid copyId;
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                copyId = document.RootElement.GetProperty("copyId").GetGuid();
                if (copyId == Guid.Empty) throw new InvalidDataException("Copy ID is empty.");
                var storedInstallationHash = document.RootElement.TryGetProperty("installationHash", out var value)
                    ? value.GetString()
                    : null;
                if (!string.Equals(storedInstallationHash, installationHash, StringComparison.Ordinal))
                {
                    copyId = Guid.NewGuid();
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new { copyId, installationHash }));
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
                or InvalidDataException or InvalidOperationException)
            {
                throw new InvalidDataException("The local Tbot copy identity is damaged. " +
                    "Remove this copy from your account before resetting it.", ex);
            }
        }
        else
        {
            copyId = Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new { copyId, installationHash }));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("TbotUltra/trial/v1/" + machineGuid));
        return new LicenseCopyIdentity(copyId, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}
