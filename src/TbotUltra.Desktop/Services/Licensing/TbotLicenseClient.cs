using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TbotUltra.Desktop.Services;

namespace TbotUltra.Desktop.Services.Licensing;

public sealed record TbotLicenseSettings(string SupabaseUrl, string PublishableKey)
{
    public static TbotLicenseSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Tbot license configuration is missing.", path);
        }

        var settings = JsonSerializer.Deserialize<TbotLicenseSettings>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (settings is null || !Uri.TryCreate(settings.SupabaseUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(settings.PublishableKey))
        {
            throw new InvalidDataException("Tbot license configuration is invalid.");
        }

        return settings;
    }
}

public sealed record TbotLicenseStatus(
    [property: JsonPropertyName("server_now")] DateTimeOffset ServerNow,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("full_access_until")] DateTimeOffset? FullAccessUntil,
    [property: JsonPropertyName("lifetime_access")] bool LifetimeAccess,
    [property: JsonPropertyName("offline_eligible")] bool OfflineEligible,
    [property: JsonPropertyName("promo_redeemed")] bool PromoRedeemed,
    [property: JsonPropertyName("promo_activation_available")] bool PromoActivationAvailable,
    [property: JsonPropertyName("pending_paid_months")] int PendingPaidMonths,
    [property: JsonPropertyName("seconds_remaining")] int SecondsRemaining,
    [property: JsonPropertyName("active_free_runs")] int ActiveFreeRuns,
    [property: JsonPropertyName("next_reset_at")] DateTimeOffset NextResetAt,
    [property: JsonPropertyName("active_copies")] int ActiveCopies,
    [property: JsonPropertyName("checkout_enabled")] bool CheckoutEnabled);

public sealed record TbotLicenseLease(
    [property: JsonPropertyName("server_now")] DateTimeOffset ServerNow,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("lease_until")] DateTimeOffset LeaseUntil);

public sealed record TbotLicenseCopy(
    [property: JsonPropertyName("copy_id")] Guid CopyId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("registered_at")] DateTimeOffset RegisteredAt);

/// <summary>Supabase Auth and the narrow license API used by both automated and manual bot runs.</summary>
public sealed class TbotLicenseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TbotLicenseSettings _settings;
    private readonly string? _sessionPath;
    private readonly string? _offlinePath;
    private readonly string _installationHash;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;
    private OfflinePaidCache? _offlinePaidCache;
    private Guid? _registeredCopyId;

    private sealed record OfflinePaidCache(Guid CopyId, string InstallationHash, DateTimeOffset VerifiedAt,
        DateTimeOffset FullAccessUntil, DateTimeOffset LastSeenAt);

    public TbotLicenseClient(TbotLicenseSettings settings, string? sessionPath = null,
        HttpClient? http = null, Action<string>? log = null)
    {
        _settings = settings;
        _sessionPath = sessionPath;
        _offlinePath = sessionPath is null ? null : Path.Combine(Path.GetDirectoryName(sessionPath)!, "license-offline.dat");
        var installationPath = sessionPath is null
            ? "in-memory"
            : Path.GetFullPath(Path.GetDirectoryName(sessionPath)!).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        _installationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(installationPath))).ToLowerInvariant();
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("apikey", settings.PublishableKey);
        if (sessionPath is not null && File.Exists(sessionPath))
        {
            try
            {
                _refreshToken = ReadProtected(sessionPath);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or IOException)
            {
                File.Delete(sessionPath);
                _log?.Invoke($"[license] Saved account session was unreadable: {ex.Message}");
            }
        }
        if (_refreshToken is not null && _offlinePath is not null && File.Exists(_offlinePath))
        {
            try
            {
                _offlinePaidCache = JsonSerializer.Deserialize<OfflinePaidCache>(ReadProtected(_offlinePath));
                _registeredCopyId = _offlinePaidCache?.CopyId;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or JsonException)
            {
                File.Delete(_offlinePath);
                _log?.Invoke($"[license] Saved offline license was unreadable: {ex.Message}");
            }
        }
    }

    public bool IsSignedIn => _refreshToken is not null;

    public void SetLocalCopy(Guid copyId)
    {
        if (copyId == Guid.Empty) throw new ArgumentException("Copy ID is empty.", nameof(copyId));
        _registeredCopyId = copyId;
    }

    public async Task SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.SupabaseUrl}/auth/v1/signup")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ClearOfflineCache();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_settings.SupabaseUrl}/auth/v1/token?grant_type=password")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await SaveSessionAsync(response, cancellationToken);
    }

    public async Task<string?> GetAccountEmailAsync(CancellationToken cancellationToken = default)
    {
        var token = await CurrentTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_settings.SupabaseUrl}/auth/v1/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var user = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return user.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
            ? email.GetString()
            : null;
    }

    public void SignOut()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = default;
        if (_sessionPath is not null && File.Exists(_sessionPath)) File.Delete(_sessionPath);
        ClearOfflineCache();
    }

    public async Task<TbotLicenseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await SendLicenseAsync<TbotLicenseStatus>(HttpMethod.Get, "status", null, cancellationToken);
            if (_registeredCopyId is { } copyId && status.OfflineEligible &&
                status.FullAccessUntil is { } until && until > status.ServerNow)
            {
                var verifiedAt = DateTimeOffset.UtcNow;
                _offlinePaidCache = new OfflinePaidCache(
                    copyId, _installationHash, verifiedAt, until, verifiedAt);
                if (_offlinePath is not null)
                {
                    WriteProtected(_offlinePath, JsonSerializer.Serialize(_offlinePaidCache));
                }
            }
            else ClearOfflineCache();
            return status;
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken) &&
                                   _offlinePaidCache is { } cached &&
                                   TryGetOfflinePaidUntil(cached.CopyId, out _))
        {
            var until = _offlinePaidCache!.FullAccessUntil;
            _log?.Invoke("[license] Showing cached paid access during a network outage.");
            return new TbotLicenseStatus(DateTimeOffset.UtcNow, "full", until, false, true,
                false, false, 0, 0, 0, DateTimeOffset.MinValue, 0, false);
        }
    }

    public async Task RegisterCopyAsync(Guid copyId, string fingerprintHash, string label,
        CancellationToken cancellationToken = default)
    {
        await SendLicenseAsync<JsonElement>(HttpMethod.Post, "register-copy",
            new { copy_id = copyId, fingerprint_hash = fingerprintHash, label }, cancellationToken);
        _registeredCopyId = copyId;
    }

    public Task RemoveCopyAsync(Guid copyId, CancellationToken cancellationToken = default)
        => SendLicenseAsync<JsonElement>(HttpMethod.Post, "remove-copy",
            new { copy_id = copyId }, cancellationToken);

    public Task RedeemCodeAsync(string code, CancellationToken cancellationToken = default)
        => SendLicenseAsync<JsonElement>(HttpMethod.Post, "redeem",
            new { code }, cancellationToken);

    public async Task<TbotLicenseLease> StartOrRenewAsync(Guid copyId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lease = await SendLicenseAsync<TbotLicenseLease>(HttpMethod.Post, "lease",
                new { copy_id = copyId, run_id = runId }, cancellationToken);
            _registeredCopyId = copyId;
            return lease;
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken) &&
                                   TryGetOfflinePaidUntil(copyId, out _))
        {
            var now = DateTimeOffset.UtcNow;
            var until = new[] { now.AddMinutes(10), _offlinePaidCache!.FullAccessUntil,
                _offlinePaidCache.VerifiedAt.AddHours(24) }.Min();
            _log?.Invoke($"[license] Using cached paid lease until {until:O} during network outage.");
            return new TbotLicenseLease(now, "offline-full", until);
        }
    }

    public Task StopAsync(Guid runId, CancellationToken cancellationToken = default)
        => SendLicenseAsync<JsonElement>(HttpMethod.Post, "stop",
            new { run_id = runId }, cancellationToken);

    public async Task<string> CreateCheckoutAsync(int months, CancellationToken cancellationToken = default)
    {
        var result = await SendLicenseAsync<JsonElement>(HttpMethod.Post, "checkout",
            new { months }, cancellationToken);
        return result.GetProperty("url").GetString()
            ?? throw new InvalidDataException("Checkout URL is missing.");
    }

    public async Task<IReadOnlyList<TbotLicenseCopy>> GetCopiesAsync(CancellationToken cancellationToken = default)
    {
        var token = await CurrentTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_settings.SupabaseUrl}/rest/v1/license_copies?select=copy_id,label,registered_at&revoked_at=is.null");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<TbotLicenseCopy>>(cancellationToken)
            ?? [];
    }

    private async Task<T> SendLicenseAsync<T>(HttpMethod method, string action, object? body,
        CancellationToken cancellationToken)
    {
        var token = await CurrentTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method,
            $"{_settings.SupabaseUrl}/functions/v1/license-api/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException($"License response for {action} is empty.");
    }

    private async Task<string> CurrentTokenAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_refreshToken is null) throw new InvalidOperationException("Sign in to your Tbot account first.");
            if (_accessToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.SupabaseUrl}/auth/v1/token?grant_type=refresh_token")
            {
                Content = JsonContent.Create(new { refresh_token = _refreshToken }),
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            await SaveSessionAsync(response, cancellationToken);
            return _accessToken!;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task SaveSessionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidDataException("The sign-in service returned an empty response. Please try again.");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The sign-in service returned an invalid response. Please try again.", ex);
        }
        using (document)
        {
            var root = document.RootElement;
            _accessToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidDataException("Access token is missing.");
            _refreshToken = root.GetProperty("refresh_token").GetString()
                ?? throw new InvalidDataException("Refresh token is missing.");
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
            if (_sessionPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
                WriteProtected(_sessionPath, _refreshToken);
            }
        }
    }

    private bool TryGetOfflinePaidUntil(Guid copyId, out DateTimeOffset until)
    {
        until = _offlinePaidCache?.FullAccessUntil ?? DateTimeOffset.MinValue;
        if (!IsSignedIn || _offlinePaidCache is not { } cache || cache.CopyId != copyId ||
            !string.Equals(cache.InstallationHash, _installationHash, StringComparison.Ordinal))
            return false;
        var now = DateTimeOffset.UtcNow;
        if (now < cache.LastSeenAt.AddMinutes(-2) ||
            now >= cache.VerifiedAt.AddHours(24) || now >= cache.FullAccessUntil)
            return false;
        _offlinePaidCache = cache with { LastSeenAt = now > cache.LastSeenAt ? now : cache.LastSeenAt };
        if (_offlinePath is not null)
            WriteProtected(_offlinePath, JsonSerializer.Serialize(_offlinePaidCache));
        return true;
    }

    private void ClearOfflineCache()
    {
        _offlinePaidCache = null;
        _registeredCopyId = null;
        if (_offlinePath is not null && File.Exists(_offlinePath)) File.Delete(_offlinePath);
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException { StatusCode: null }
            or HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout }
            or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
            or HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static string ReadProtected(string path)
    {
        var encrypted = Convert.FromBase64String(File.ReadAllText(path));
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted,
            null, DataProtectionScope.CurrentUser));
    }

    private static void WriteProtected(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(content),
            null, DataProtectionScope.CurrentUser);
        AtomicFile.WriteAllText(path, Convert.ToBase64String(encrypted));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? detail = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var field in new[] { "error_description", "msg", "error", "message" })
            {
                if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    detail = value.GetString();
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // The service may return plain text for gateway errors.
        }

        detail ??= $"License request failed ({(int)response.StatusCode}).";
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || response.StatusCode >= HttpStatusCode.InternalServerError)
        {
            throw new HttpRequestException(detail, null, response.StatusCode);
        }

        throw new InvalidOperationException(detail);
    }

    public void Dispose()
    {
        _sessionGate.Dispose();
        _http.Dispose();
    }
}
