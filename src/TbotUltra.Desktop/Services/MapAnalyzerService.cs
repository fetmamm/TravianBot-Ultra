using System.IO;
using System.Net.Http;
using TbotUltra.Desktop.Models;

namespace TbotUltra.Desktop.Services;

public sealed class MapAnalyzerService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    private readonly string _projectRoot;
    private readonly Action<string>? _log;

    public MapAnalyzerService(string projectRoot, Action<string>? log = null)
    {
        _projectRoot = projectRoot;
        _log = log;
    }

    public Task<List<OasisInfo>> AnalyzeAsync(
        string serverUrl,
        bool includeOccupied,
        List<string> selectedTypes)
    {
        return AnalyzeAsync(serverUrl, includeOccupied, selectedTypes, CancellationToken.None);
    }

    public async Task<List<OasisInfo>> AnalyzeAsync(
        string serverUrl,
        bool includeOccupied,
        List<string> selectedTypes,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("The active Travian server URL is invalid.");
            }

            if (selectedTypes.Count == 0)
            {
                throw new InvalidOperationException("Select at least one oasis type.");
            }

            var mapUri = new Uri($"{serverUri.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/map.sql");
            var directory = Path.Combine(_projectRoot, "Data", "Maps", SanitizePathPart(serverUri.Host));
            var path = Path.Combine(directory, "map.sql");

            _log?.Invoke($"[map-oasis] starting map.sql download from '{mapUri}'.");
            Directory.CreateDirectory(directory);
            using (var response = await HttpClient.GetAsync(
                       mapUri,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    useAsync: true);
                await source.CopyToAsync(destination, cancellationToken);
            }

            _log?.Invoke($"[map-oasis] map.sql saved to '{path}'.");
            _log?.Invoke("[map-oasis] parsing map.sql.");
            var oases = MapOasisParser.Parse(
                File.ReadLines(path),
                includeOccupied,
                selectedTypes);
            _log?.Invoke($"[map-oasis] parsed {oases.Count} matching oases.");
            return oases;
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("[map-oasis] analysis canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[map-oasis] analysis failed: {ex}");
            throw new InvalidOperationException($"Map oasis analysis failed: {ex.Message}", ex);
        }
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
