using System.Net;
using GroupBuy.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection;

namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    private sealed class UnavailableProtection : IDataProtectionProvider {
        public IDataProtector CreateProtector(string purpose) => throw new System.Security.Cryptography.CryptographicException("synthetic-sensitive-startup-message");
    }
    [Fact, Trait("Sprint5", "Security")]
    public void Host_fails_closed_with_a_safe_error_when_factor_key_protection_is_unavailable()
    {
        using var basis = new ApiFactory(fixture.Connection);
        using var f = basis.WithWebHostBuilder(builder => builder.ConfigureServices(s => {
            s.RemoveAll<IDataProtectionProvider>(); s.AddSingleton<IDataProtectionProvider>(new UnavailableProtection());
        }));
        var error = Assert.Throws<InvalidOperationException>(() => f.CreateClient());
        Assert.Equal("Data protection is unavailable; verify key storage and certificate configuration.", error.Message);
        Assert.Null(error.InnerException);
    }
    [Fact, Trait("Sprint5", "Security")]
    public async Task Graceful_host_shutdown_stops_the_existing_worker()
    {
        var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        var health = f.Services.GetRequiredService<WorkerHealthState>();
        await f.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(health.Read().Running);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public async Task Liveness_stays_safe_while_readiness_detects_database_outage_and_recovery()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var health = f.Services.GetRequiredService<WorkerHealthState>(); health.Completed(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var config = f.Services.GetRequiredService<IConfiguration>();
        config["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=fixture;Timeout=1;Command Timeout=1";
        var unavailable = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var body = await unavailable.Content.ReadAsStringAsync(); Assert.DoesNotContain("fixture", body); Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("Host", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        config["ConnectionStrings:Database"] = fixture.Connection;
        await Tick(f); health.Completed(DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        health.Failed("TestFailure"); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
    [Fact]
    [Trait("Sprint5", "Security")]
    public void Proxy_configuration_requires_explicit_IPs_and_one_symmetric_hop()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient();
        var disabled = f.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        Assert.Equal(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None, disabled.ForwardedHeaders);
        using var known = new ApiFactory(fixture.Connection, overrides: new() { ["Proxy:KnownProxies:0"] = "10.20.30.40" }); using var other = known.CreateClient();
        var enabled = known.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        Assert.Equal(System.Net.IPAddress.Parse("10.20.30.40"), Assert.Single(enabled.KnownProxies)); Assert.Empty(enabled.KnownIPNetworks);
        Assert.Equal(1, enabled.ForwardLimit); Assert.True(enabled.RequireHeaderSymmetry);
        Assert.Equal(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto, enabled.ForwardedHeaders);
    }
}

