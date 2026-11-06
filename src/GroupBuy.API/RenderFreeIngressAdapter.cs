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
        {
            if (ExternalHostname is not null)
            {
                LogInputs(context);
                context.RequestServices.GetRequiredService<ILogger<RenderFreeIngressAdapter>>()
                    .LogInformation("Render Free ingress outcome: SchemeReconstructed={SchemeReconstructed}", false);
            }
            return next(context);
        }

        var peer = context.Connection.RemoteIpAddress;
        if (peer is { IsIPv4MappedToIPv6: true }) peer = peer.MapToIPv4();
        var reconstructed = false;
        LogInputs(context);
        if (context.Request.Scheme == "http" && peer is not null && !IPAddress.IsLoopback(peer)
            && context.Connection.LocalPort == Port
            && string.Equals(context.Request.Host.Host, ExternalHostname, StringComparison.OrdinalIgnoreCase)
            && context.Request.Host.Port is null or 443)
        {
            // No request header is evidence for this decision. The acknowledged platform isolation is.
            context.Request.Scheme = "https";
            reconstructed = true;
        }
        context.RequestServices.GetRequiredService<ILogger<RenderFreeIngressAdapter>>()
            .LogInformation("Render Free ingress outcome: SchemeReconstructed={SchemeReconstructed}", reconstructed);
        return next(context);
    }

    // Temporary diagnostics: keep this explicit allowlist; never log the request or connection object.
    private void LogInputs(HttpContext context)
    {
        var originalPeer = context.Connection.RemoteIpAddress;
        var mapped = originalPeer is { IsIPv4MappedToIPv6: true };
        var peer = mapped ? originalPeer!.MapToIPv4() : originalPeer;
        var loopback = peer is not null && IPAddress.IsLoopback(peer);
        context.RequestServices.GetRequiredService<ILogger<RenderFreeIngressAdapter>>().LogInformation(
            "Render Free ingress inputs: Scheme={Scheme} Host={Host} HostPort={HostPort} LocalPort={LocalPort} " +
            "RemoteIpIsNull={RemoteIpIsNull} RemoteIpIsLoopback={RemoteIpIsLoopback} RemoteIpIsIPv4MappedIPv6={RemoteIpIsIPv4MappedIPv6} " +
            "ConfiguredPort={ConfiguredPort} ExternalHostname={ExternalHostname} " +
            "ExternalHostnameConfigured={ExternalHostnameConfigured} IsLiveHealthPath={IsLiveHealthPath} IsReadyHealthPath={IsReadyHealthPath} " +
            "SchemeIsHttp={SchemeIsHttp} RemoteIpPresent={RemoteIpPresent} RemoteIpNotLoopback={RemoteIpNotLoopback} " +
            "LocalPortMatches={LocalPortMatches} HostMatches={HostMatches} HostPortAllowed={HostPortAllowed}",
            context.Request.Scheme, context.Request.Host.Host, context.Request.Host.Port, context.Connection.LocalPort,
            originalPeer is null, loopback, mapped, Port, ExternalHostname,
            ExternalHostname is not null, context.Request.Path == "/health/live", context.Request.Path == "/health/ready",
            context.Request.Scheme == "http", peer is not null, !loopback,
            context.Connection.LocalPort == Port,
            string.Equals(context.Request.Host.Host, ExternalHostname, StringComparison.OrdinalIgnoreCase),
            context.Request.Host.Port is null or 443);
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
