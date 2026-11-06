using System.Net;

namespace GroupBuy.API;

/// <summary>
/// External scheme reconstruction under Render Free's public-edge-only topology.
/// This is NOT proxy/header authentication. Never enable where direct/private ingress exists.
/// </summary>
public sealed record RenderFreeIngressAdapter(string? ExternalHostname, int Port)
{
    public static RenderFreeIngressAdapter Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Hosting:RenderFreeIngress");
        if (!section.GetValue("Enabled", false)) return new(null, 0);

        var hostname = section["ExternalHostname"];
        var allowedHosts = configuration["AllowedHosts"];
        if (!environment.IsProduction()
            || !section.GetValue("PublicEdgeIsolationAcknowledged", false)
            || configuration["RENDER"] != "true"
            || configuration["RENDER_SERVICE_TYPE"] != "web"
            || string.IsNullOrEmpty(hostname)
            || hostname.Length > 253
            || Uri.CheckHostName(hostname) != UriHostNameType.Dns
            || !hostname.EndsWith(".onrender.com", StringComparison.OrdinalIgnoreCase)
            || hostname.Split('.').Any(label => label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])
                || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            || !string.Equals(allowedHosts, hostname, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration["RENDER_EXTERNAL_HOSTNAME"], hostname, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration["RENDER_EXTERNAL_URL"], $"https://{hostname}", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(configuration["PORT"], out var port) || port is < 1024 or > 65535
            || configuration.GetSection("Proxy:KnownProxies").Get<string[]>() is { Length: > 0 }
            || string.Equals(configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Render Free ingress requires Production, explicit public-edge isolation acknowledgement, matching Render web-service HTTPS identity and sole AllowedHosts hostname, PORT, and no proxy forwarding configuration.");

        return new(hostname, port);
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Probe behavior stays independent of ingress reconstruction, including local CLI checks.
        if (ExternalHostname is null || context.Request.Path == "/health/live" || context.Request.Path == "/health/ready")
            return next(context);

        var peer = context.Connection.RemoteIpAddress;
        if (peer is { IsIPv4MappedToIPv6: true }) peer = peer.MapToIPv4();
        if (context.Request.Scheme == "http" && peer is not null && !IPAddress.IsLoopback(peer)
            && context.Connection.LocalPort == Port
            && string.Equals(context.Request.Host.Host, ExternalHostname, StringComparison.OrdinalIgnoreCase)
            && context.Request.Host.Port is null or 443)
        {
            // No request header is evidence for this decision. The acknowledged platform isolation is.
            context.Request.Scheme = "https";
        }
        return next(context);
    }
}

public static class RenderFreeIngressExtensions
{
    public static IApplicationBuilder UseRenderFreeIngress(this IApplicationBuilder app)
    {
        var adapter = app.ApplicationServices.GetRequiredService<RenderFreeIngressAdapter>();
        return app.Use(next => context => adapter.InvokeAsync(context, next));
    }
}
