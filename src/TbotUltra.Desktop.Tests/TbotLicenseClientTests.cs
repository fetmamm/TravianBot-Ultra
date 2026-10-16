using System.Net;
using System.Net.Http;
using System.Text;
using TbotUltra.Desktop.Services.Licensing;
using Xunit;

namespace TbotUltra.Desktop.Tests;

public sealed class TbotLicenseClientTests
{
    [Fact]
    public async Task EmptySignInResponseHasHelpfulMessage()
    {
        using var http = new HttpClient(new ReplyHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
            })));
        using var client = new TbotLicenseClient(
            new TbotLicenseSettings("https://example.supabase.co", "publishable-key"), http: http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.SignInAsync("person@example.com", "password"));

        Assert.Equal("The sign-in service returned an empty response. Please try again.", error.Message);
    }

    [Fact]
    public async Task SavedSessionIsUsedAfterClientRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tbot-license-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "license-session.dat");
        try
        {
            using (var firstHttp = new HttpClient(new ReplyHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"access_token":"first-token","refresh_token":"saved-refresh-token","expires_in":3600}
                        """, Encoding.UTF8, "application/json"),
                }))))
            using (var firstClient = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"),
                sessionPath, firstHttp))
            {
                await firstClient.SignInAsync("person@example.com", "password");
            }

            var refreshBody = string.Empty;
            using var resumedHttp = new HttpClient(new ReplyHandler(async request =>
            {
                if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
                {
                    refreshBody = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {"access_token":"resumed-token","refresh_token":"rotated-refresh-token","expires_in":3600}
                            """, Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"server_now":"2026-10-16T12:00:00Z","tier":"full","full_access_until":null,
                         "lifetime_access":true,"offline_eligible":true,"promo_redeemed":false,
                         "promo_activation_available":false,"pending_paid_months":0,"seconds_remaining":0,
                         "active_free_runs":0,"next_reset_at":"2026-10-16T22:00:00Z","active_copies":1,
                         "checkout_enabled":true}
                        """, Encoding.UTF8, "application/json"),
                };
            }));
            using var resumedClient = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"),
                sessionPath, resumedHttp);

            Assert.True(resumedClient.IsSignedIn);
            var status = await resumedClient.GetStatusAsync();

            Assert.True(status.LifetimeAccess);
            Assert.Contains("saved-refresh-token", refreshBody, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AccountEmailRemainsAvailableAfterSignOutAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tbot-license-{Guid.NewGuid():N}");
        var sessionPath = Path.Combine(directory, "license-session.dat");
        try
        {
            using var http = new HttpClient(new ReplyHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"access_token":"token","refresh_token":"refresh-token","expires_in":3600}
                        """, Encoding.UTF8, "application/json"),
                })));
            using (var client = new TbotLicenseClient(
                       new TbotLicenseSettings("https://example.supabase.co", "publishable-key"),
                       sessionPath, http))
            {
                await client.SignInAsync("person@example.com", "password");
                client.SignOut();
                Assert.Equal("person@example.com", client.RememberedEmail);
            }

            using var restartedClient = new TbotLicenseClient(
                new TbotLicenseSettings("https://example.supabase.co", "publishable-key"),
                sessionPath, new HttpClient(new ReplyHandler(_ => throw new InvalidOperationException())));

            Assert.False(restartedClient.IsSignedIn);
            Assert.Equal("person@example.com", restartedClient.RememberedEmail);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SignedInCopyCanReadStatusAndStartAndStopLease()
    {
        var calls = new List<(string Path, string? Authorization, string? Body)>();
        using var http = new HttpClient(new ReplyHandler(async request =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            var body = request.RequestUri.AbsolutePath switch
            {
                "/auth/v1/token" => """
                    {"access_token":"user-token","refresh_token":"refresh-token","expires_in":3600}
                    """,
                "/functions/v1/license-api/register-copy" => "{" + "\"ok\":true}",
                "/functions/v1/license-api/status" => """
                    {"server_now":"2026-10-15T12:00:00Z","tier":"pending","full_access_until":null,
                     "promo_redeemed":false,"promo_activation_available":false,
                     "pending_paid_months":0,"seconds_remaining":10800,
                     "active_free_runs":2,"next_reset_at":"2026-10-15T22:00:00Z","active_copies":1,
                     "checkout_enabled":true}
                    """,
                "/functions/v1/license-api/lease" => """
                    {"server_now":"2026-10-15T12:00:00Z","tier":"full",
                     "lease_until":"2026-10-15T12:10:00Z"}
                    """,
                "/functions/v1/license-api/stop" => "{" + "\"ok\":true}",
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }));
        using var client = new TbotLicenseClient(
            new TbotLicenseSettings("https://example.supabase.co", "publishable-key"), http: http);

        await client.SignInAsync("person@example.com", "password");
        var copyId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        await client.RegisterCopyAsync(copyId, new string('a', 64), "My copy");
        var status = await client.GetStatusAsync();
        var lease = await client.StartOrRenewAsync(copyId, runId);
        await client.StopAsync(runId);

        Assert.Equal("pending", status.Tier);
        Assert.Equal(10800, status.SecondsRemaining);
        Assert.Equal(2, status.ActiveFreeRuns);
        Assert.True(status.CheckoutEnabled);
        Assert.Equal("full", lease.Tier);
        Assert.Equal(5, calls.Count);
        Assert.Equal("Bearer user-token", calls[2].Authorization);
        Assert.Contains(copyId.ToString(), calls[1].Body);
        Assert.Contains(runId.ToString(), calls[3].Body);
    }

    [Fact]
    public async Task ServerDenialPreventsLease()
    {
        using var http = new HttpClient(new ReplyHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/auth/v1/token"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"access_token":"user-token","refresh_token":"refresh-token","expires_in":3600}
                        """, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"Daily free limit reached\"}",
                        Encoding.UTF8, "application/json"),
                })));
        using var client = new TbotLicenseClient(
            new TbotLicenseSettings("https://example.supabase.co", "publishable-key"), http: http);
        await client.SignInAsync("person@example.com", "password");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartOrRenewAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("Daily free limit reached", error.Message);
    }

    [Fact]
    public async Task RecentPaidStatusAllowsOnlyShortOfflineLease()
    {
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lease", StringComparison.Ordinal))
                throw new HttpRequestException("Network unavailable");
            var body = path.EndsWith("/status", StringComparison.Ordinal)
                ? $$"""
                    {"server_now":"{{DateTimeOffset.UtcNow:O}}","tier":"full",
                     "full_access_until":"{{DateTimeOffset.UtcNow.AddDays(20):O}}",
                     "offline_eligible":true,"promo_redeemed":false,
                     "promo_activation_available":false,"pending_paid_months":0,
                     "seconds_remaining":10800,"active_free_runs":0,"next_reset_at":"2026-10-16T00:00:00Z",
                     "active_copies":1}
                    """
                : path.EndsWith("/register-copy", StringComparison.Ordinal) ? "{\"ok\":true}" : """
                    {"access_token":"user-token","refresh_token":"refresh-token","expires_in":3600}
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }));
        using var client = new TbotLicenseClient(
            new TbotLicenseSettings("https://example.supabase.co", "publishable-key"), http: http);
        await client.SignInAsync("person@example.com", "password");
        var copyId = Guid.NewGuid();
        await client.RegisterCopyAsync(copyId, new string('a', 64), "My copy");
        await client.GetStatusAsync();

        var lease = await client.StartOrRenewAsync(copyId, Guid.NewGuid());

        Assert.Equal("offline-full", lease.Tier);
        Assert.InRange(lease.LeaseUntil - lease.ServerNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task PaidOfflineLeaseIsLimitedToTransientFailures(HttpStatusCode failureStatus,
        bool shouldUseOfflineLease)
    {
        var leaseRequested = false;
        using var http = new HttpClient(new ReplyHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lease", StringComparison.Ordinal))
            {
                leaseRequested = true;
                return Task.FromResult(new HttpResponseMessage(failureStatus)
                {
                    Content = new StringContent("{\"error\":\"License service unavailable\"}",
                        Encoding.UTF8, "application/json"),
                });
            }

            var body = path.EndsWith("/status", StringComparison.Ordinal)
                ? $$"""
                    {"server_now":"{{DateTimeOffset.UtcNow:O}}","tier":"full",
                     "full_access_until":"{{DateTimeOffset.UtcNow.AddDays(20):O}}",
                     "offline_eligible":true,"promo_redeemed":false,
                     "promo_activation_available":false,"pending_paid_months":0,
                     "seconds_remaining":10800,"active_free_runs":0,"next_reset_at":"2026-10-16T00:00:00Z",
                     "active_copies":1}
                    """
                : path.EndsWith("/register-copy", StringComparison.Ordinal) ? "{\"ok\":true}" : """
                    {"access_token":"user-token","refresh_token":"refresh-token","expires_in":3600}
                    """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }));
        using var client = new TbotLicenseClient(
            new TbotLicenseSettings("https://example.supabase.co", "publishable-key"), http: http);
        await client.SignInAsync("person@example.com", "password");
        var copyId = Guid.NewGuid();
        await client.RegisterCopyAsync(copyId, new string('a', 64), "My copy");
        await client.GetStatusAsync();

        if (shouldUseOfflineLease)
        {
            var lease = await client.StartOrRenewAsync(copyId, Guid.NewGuid());
            Assert.Equal("offline-full", lease.Tier);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.StartOrRenewAsync(copyId, Guid.NewGuid()));
            Assert.Equal("License service unavailable", error.Message);
        }

        Assert.True(leaseRequested);
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => reply(request);
    }
}
