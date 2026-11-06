using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using GroupBuy.API;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GroupBuy.IntegrationTests;

public sealed class RenderFreeIngressTests
{
    private const string Host = "ingress-proof.onrender.com";
    private const string Origin = "https://groudeal-frontend.vercel.app";

    private sealed class DiagnosticLogger : ILogger<RenderFreeIngressAdapter>
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Information, level);
            Assert.Null(exception);
            Events.Add(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary(p => p.Key, p => p.Value));
        }
    }

    [Theory]
    [InlineData(true, "198.51.100.20", "/api/probe", true)]
    [InlineData(true, "::ffff:127.0.0.1", "/api/probe", false)]
    [InlineData(true, null, "/api/probe", false)]
    [InlineData(true, "198.51.100.20", "/health/live", false)]
    [InlineData(true, "198.51.100.20", "/health/ready", false)]
    [InlineData(false, "198.51.100.20", "/api/probe", false)]
    public async Task Temporary_diagnostics_log_only_allowlisted_scalars_and_actual_outcome(bool enabled, string? address, string path, bool reconstructed)
    {
        var logger = new DiagnosticLogger();
        using var services = new ServiceCollection().AddSingleton<ILogger<RenderFreeIngressAdapter>>(logger).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http"; context.Request.Host = new HostString(Host); context.Request.Path = path;
        context.Connection.LocalPort = 10000;
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        var originalPeer = context.Connection.RemoteIpAddress;
        const string canary = "synthetic-do-not-log-canary";
        context.Request.QueryString = new QueryString("?private=" + canary);
        foreach (var header in new[] { "X-Forwarded-For", "X-Forwarded-Proto", "Forwarded", "Authorization", "Cookie" })
            context.Request.Headers[header] = canary;
        var nextCalled = false;
        await new RenderFreeIngressAdapter(enabled ? Host : null, 10000).InvokeAsync(context, _ => { nextCalled = true; return Task.CompletedTask; });
        Assert.True(nextCalled);
        Assert.Equal(reconstructed ? "https" : "http", context.Request.Scheme);
        Assert.Equal(originalPeer, context.Connection.RemoteIpAddress);
        if (!enabled) { Assert.Empty(logger.Events); return; }
        Assert.Equal(2, logger.Events.Count);
        var inputs = logger.Events[0];
        string[] expected = ["Scheme", "Host", "HostPort", "LocalPort", "RemoteIpIsNull", "RemoteIpIsLoopback", "RemoteIpIsIPv4MappedIPv6",
            "ConfiguredPort", "ExternalHostname", "ExternalHostnameConfigured", "IsLiveHealthPath", "IsReadyHealthPath", "SchemeIsHttp",
            "RemoteIpPresent", "RemoteIpNotLoopback", "LocalPortMatches", "HostMatches", "HostPortAllowed", "{OriginalFormat}"];
        Assert.Equal(expected.Order(), inputs.Keys.Order());
        Assert.Equal("http", inputs["Scheme"]); Assert.Equal(Host, inputs["Host"]); Assert.Null(inputs["HostPort"]);
        Assert.Equal(10000, inputs["LocalPort"]); Assert.Equal(10000, inputs["ConfiguredPort"]); Assert.Equal(Host, inputs["ExternalHostname"]);
        Assert.Equal(address is null, inputs["RemoteIpIsNull"]);
        Assert.Equal(address?.StartsWith("::ffff:") == true, inputs["RemoteIpIsIPv4MappedIPv6"]);
        Assert.Equal(address == "::ffff:127.0.0.1", inputs["RemoteIpIsLoopback"]);
        Assert.Equal(new[] { "SchemeReconstructed", "{OriginalFormat}" }.Order(), logger.Events[1].Keys.Order());
        Assert.Equal(reconstructed, logger.Events[1]["SchemeReconstructed"]);
        foreach (var value in logger.Events.SelectMany(e => e.Values))
        {
            Assert.True(value is null or string or int or bool);
            Assert.DoesNotContain(canary, value?.ToString() ?? "");
            if (address is not null) Assert.DoesNotContain(address, value?.ToString() ?? "");
        }
    }
    private static WebApplicationBuilder Builder(bool enabled = true, Dictionary<string, string?>? overrides = null, string environment = "Production")
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        b.Configuration.Sources.Clear();
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:RenderFreeIngress:Enabled"] = enabled.ToString(),
            ["Hosting:RenderFreeIngress:PublicEdgeIsolationAcknowledged"] = "true",
            ["Hosting:RenderFreeIngress:ExternalHostname"] = Host,
            ["AllowedHosts"] = Host, ["PORT"] = "10000", ["RENDER"] = "true",
            ["RENDER_SERVICE_TYPE"] = "web", ["RENDER_EXTERNAL_HOSTNAME"] = Host,
            ["RENDER_EXTERNAL_URL"] = $"https://{Host}"
        }.Concat(overrides ?? new()).GroupBy(p => p.Key).Select(g => g.Last()));
        return b;
    }

    private static async Task<WebApplication> Start(bool enabled = true, string peer = "198.51.100.20", int port = 10000)
    {
        var b = Builder(enabled);
        b.WebHost.UseTestServer();
        b.Services.AddSingleton(RenderFreeIngressAdapter.Validate(b.Configuration, b.Environment));
        b.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardLimit = 1; o.RequireHeaderSymmetry = true; });
        b.Services.AddHttpsRedirection(o => o.HttpsPort = 443);
        b.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(30));
        b.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(Origin).AllowAnyHeader().AllowAnyMethod()));
        b.Services.AddAuthentication("Bearer").AddJwtBearer();
        b.Services.AddAuthorization();
        b.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("peer", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress!.ToString(),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) }));
        });
        var app = b.Build();
        app.Use(async (c, next) => { c.Connection.RemoteIpAddress = IPAddress.Parse(peer); c.Connection.LocalPort = port; await next(); });
        app.UseForwardedHeaders();
        app.UseRenderFreeIngress();
        app.UseHsts();
        app.UseWhen(c => c.Request.Path != "/health/live" && c.Request.Path != "/health/ready", branch => branch.UseHttpsRedirection());
        app.UseCors(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
        app.MapGet("/api/probe", (HttpContext c) => new { scheme = c.Request.Scheme, peer = c.Connection.RemoteIpAddress!.ToString(), host = c.Request.Host.Value });
        app.MapGet("/api/private", () => "protected").RequireAuthorization();
        app.MapGet("/api/limited", () => "ok").RequireRateLimiting("peer");
        app.MapGet("/health/live", (HttpContext c) => new { scheme = c.Request.Scheme, status = "live" });
        app.MapGet("/health/ready", (HttpContext c) => new { scheme = c.Request.Scheme, status = "ready" });
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Request(string path = "/api/probe", string? origin = Origin, string host = Host)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, $"http://{host}{path}");
        if (origin is not null) r.Headers.Add("Origin", origin);
        return r;
    }

    [Theory]
    [InlineData("https")]
    [InlineData("http")]
    [InlineData("https, http")]
    [InlineData("invalid")]
    public async Task Render_edge_normalizes_scheme_without_trusting_headers_or_changing_peer(string suppliedProto)
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        using var r = Request();
        r.Headers.Add("X-Forwarded-Proto", suppliedProto);
        r.Headers.Add("X-Forwarded-For", "203.0.113.17");
        r.Headers.Add("X-Forwarded-Host", "attacker.invalid");
        r.Headers.Add("Forwarded", "for=203.0.113.17;proto=http;host=attacker.invalid");
        using var response = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(response.Headers.Location);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("https", result.GetProperty("scheme").GetString());
        Assert.Equal("198.51.100.20", result.GetProperty("peer").GetString());
        Assert.Equal(Host, result.GetProperty("host").GetString());
        Assert.Equal(Origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Render_edge_without_forwarded_headers_executes_CORS_and_authentication()
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        using var r = Request(); using var response = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var privateRequest = Request("/api/private"); using var denied = await client.SendAsync(privateRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(Origin, Assert.Single(denied.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData(Origin, true)]
    [InlineData("https://attacker.invalid", false)]
    public async Task CORS_and_preflight_remain_restricted(string origin, bool allowed)
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Options })
        {
            using var r = Request(origin: origin); r.Method = method;
            if (method == HttpMethod.Options) r.Headers.Add("Access-Control-Request-Method", "GET");
            using var response = await client.SendAsync(r);
            Assert.Null(response.Headers.Location);
            Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
            if (allowed) Assert.Equal(Origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        }
    }

    [Theory]
    [InlineData(false, "198.51.100.20", 10000, Host)]
    [InlineData(true, "127.0.0.1", 10000, Host)]
    [InlineData(true, "::ffff:127.0.0.1", 10000, Host)]
    [InlineData(true, "::1", 10000, Host)]
    [InlineData(true, "198.51.100.20", 10001, Host)]
    [InlineData(true, "198.51.100.20", 10000, Host + ":10000")]
    public async Task Ineligible_HTTP_cannot_use_spoofed_forwarding_to_bypass_redirect(bool enabled, string peer, int port, string host)
    {
        await using var app = await Start(enabled, peer, port); using var client = app.GetTestClient();
        using var r = Request(host: host); r.Headers.Add("X-Forwarded-Proto", "https");
        r.Headers.Add("X-Forwarded-For", "203.0.113.17"); r.Headers.Add("Forwarded", "proto=https;for=203.0.113.17");
        using var response = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal($"https://{Host}/api/probe", response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task Arbitrary_host_is_rejected_and_cannot_activate_adapter()
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        using var r = Request(host: "attacker.invalid"); using var response = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_HTTP_is_neither_redirected_nor_normalized(string path)
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        using var r = Request(path); using var response = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("scheme").GetString());
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Forged_client_addresses_do_not_reset_rate_budget()
    {
        await using var app = await Start(); using var client = app.GetTestClient();
        for (var i = 0; i < 3; i++)
        {
            using var r = Request("/api/limited"); r.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            using var response = await client.SendAsync(r);
            Assert.Equal(i < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", null)]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", "*.onrender.com")]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", "https://ingress-proof.onrender.com")]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", "ingress-proof.onrender.com:443")]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", "ingress-proof.onrender.com/path")]
    [InlineData("Hosting:RenderFreeIngress:ExternalHostname", "ingress-proof.onrender.com.")]
    [InlineData("Hosting:RenderFreeIngress:PublicEdgeIsolationAcknowledged", "false")]
    [InlineData("AllowedHosts", "other.onrender.com")]
    [InlineData("AllowedHosts", Host + ";other.onrender.com")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("RENDER", "false")]
    [InlineData("RENDER_SERVICE_TYPE", "pserv")]
    [InlineData("RENDER_EXTERNAL_HOSTNAME", "other.onrender.com")]
    [InlineData("RENDER_EXTERNAL_URL", "http://ingress-proof.onrender.com")]
    [InlineData("RENDER_EXTERNAL_URL", "https://ingress-proof.onrender.com/path")]
    [InlineData("PORT", null)]
    [InlineData("PORT", "80")]
    [InlineData("PORT", "invalid")]
    [InlineData("Proxy:KnownProxies:0", "10.20.30.40")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")]
    public void Inconsistent_opt_in_fails_at_configuration(string key, string? value)
    {
        var b = Builder(overrides: new() { [key] = value });
        Assert.Throws<InvalidOperationException>(() => ProductionConfiguration.Configure(b));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void Mode_requires_Production(string environment)
    {
        var b = Builder(environment: environment);
        Assert.Throws<InvalidOperationException>(() => RenderFreeIngressAdapter.Validate(b.Configuration, b.Environment));
    }

    [Fact]
    public void Render_marker_alone_does_not_enable_mode()
    {
        var b = Builder(false);
        Assert.Null(RenderFreeIngressAdapter.Validate(b.Configuration, b.Environment).ExternalHostname);
    }
}
