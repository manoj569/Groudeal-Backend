using System.Net;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

namespace GroupBuy.API;

public static class ProductionConfiguration
{
    public static void ConfigurePort(WebApplicationBuilder builder)
    {
        // PORT comes from the deployment environment, never from request headers.
        if (builder.Configuration["PORT"] is not string value) return;
        if (!int.TryParse(value, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("PORT must be an unprivileged TCP port between 1024 and 65535.");
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
    }
    public static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment()) builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
        var c = builder.Configuration; var production = !builder.Environment.IsDevelopment();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.Configure<ForwardedHeadersOptions>(o => {
            o.ForwardLimit = 1; o.RequireHeaderSymmetry = true;
            var proxies = c.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
            if (proxies.Length > 0) {
                o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
                foreach (var proxy in proxies) { if (!IPAddress.TryParse(proxy, out var address)) throw new InvalidOperationException("Proxy addresses must be explicit IP addresses."); o.KnownProxies.Add(address); }
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            }
        });
        DataProtectionSetup.Configure(builder.Services, c, builder.Environment);
        if (production) {
            if (string.Equals(c["ASPNETCORE_FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use explicit Proxy__KnownProxies; unrestricted forwarded-header auto-configuration is forbidden.");
            if (!string.IsNullOrEmpty(c["Demo:Password"]) || !string.IsNullOrEmpty(c["Demo:AdminAuthenticatorKey"])) throw new InvalidOperationException("Development fixture configuration must be absent in Production.");
            var connection = new NpgsqlConnectionStringBuilder(c.GetConnectionString("Database"));
            if (connection.SslMode != SslMode.VerifyFull) throw new InvalidOperationException("Production PostgreSQL must use SslMode=VerifyFull.");
            if (connection.IncludeErrorDetail) throw new InvalidOperationException("Production database error detail must remain disabled.");
            if (!connection.Pooling || connection.MaxPoolSize is < 1 or > 200 || connection.Timeout is < 1 or > 30 || connection.CommandTimeout is < 1 or > 60) throw new InvalidOperationException("Production database pooling/timeouts are outside supported bounds.");
            if (string.IsNullOrWhiteSpace(c["AllowedHosts"]) || c["AllowedHosts"]!.Contains('*')) throw new InvalidOperationException("Production requires explicit AllowedHosts.");
            if (c["AllowedHosts"]!.Split(';').Any(h => string.IsNullOrWhiteSpace(h) || Uri.CheckHostName(h.Trim()) == UriHostNameType.Unknown))
                throw new InvalidOperationException("AllowedHosts must contain explicit hostnames separated by semicolons, without schemes or ports.");
            if (c.GetValue("AdminProvisioning:Enabled", false) && c["admin-operation"] is null) throw new InvalidOperationException("Provisioning permission must not remain enabled on the API host.");
        }
        var attempts = c.GetValue("Operations:WorkerMaxAttempts", 10);
        if (attempts is < 1 or > 100) throw new InvalidOperationException("WorkerMaxAttempts must be between 1 and 100.");
    }
}


