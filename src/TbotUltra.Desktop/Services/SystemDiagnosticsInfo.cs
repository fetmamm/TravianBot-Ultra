using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace TbotUltra.Desktop.Services;

internal static class SystemDiagnosticsInfo
{
    internal static IReadOnlyList<string> BuildLines(string appVersion, string projectRoot, DateTimeOffset generatedUtc)
    {
        var process = Process.GetCurrentProcess();
        var driveInfo = TryGetDriveInfo(projectRoot);

        return
        [
            $"GeneratedLocal: {generatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}",
            $"GeneratedUtc: {generatedUtc:O}",
            $"ProcessStartedLocal: {TryGetProcessStartTime(process)}",
            $"ProjectRoot: {projectRoot}",
            $"AppBaseDirectory: {AppContext.BaseDirectory}",
            $"AppVersion: {Normalize(appVersion)}",
            $"MachineName: {Environment.MachineName}",
            $"UserName: {Environment.UserName}",
            $"OS: {RuntimeInformation.OSDescription}",
            $"OSArchitecture: {RuntimeInformation.OSArchitecture}",
            $"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}",
            $"DotNet: {RuntimeInformation.FrameworkDescription}",
            $"CPU: {ReadCpuDescription()}",
            $"LogicalProcessors: {Environment.ProcessorCount}",
            $"RAM: {ReadRamDescription()}",
            $"Screen: {(int)SystemParameters.PrimaryScreenWidth}x{(int)SystemParameters.PrimaryScreenHeight}",
            $"Culture: {CultureInfo.CurrentCulture.Name}",
            $"UICulture: {CultureInfo.CurrentUICulture.Name}",
            $"TimeZone: {TimeZoneInfo.Local.Id} ({TimeZoneInfo.Local.BaseUtcOffset})",
            $"CurrentDirectory: {Environment.CurrentDirectory}",
            $"DriveFreeSpace: {driveInfo}",
        ];
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    private static string TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "unknown";
        }
    }

    private static string TryGetDriveInfo(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return "unknown";
            }

            var drive = new DriveInfo(root);
            return $"{drive.AvailableFreeSpace / (1024d * 1024d * 1024d):F1} GB free of {drive.TotalSize / (1024d * 1024d * 1024d):F1} GB";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadCpuDescription()
    {
        try
        {
            return Normalize(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty);
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadRamDescription()
    {
        try
        {
            var memoryStatus = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
            {
                return "unknown";
            }

            return $"{memoryStatus.TotalPhys / (1024d * 1024d * 1024d):F1} GB";
        }
        catch
        {
            return "unknown";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx memoryStatus);
}
