using System.Net;
using System.Security.Cryptography.X509Certificates;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

namespace GroupBuy.API;

public static class ProductionConfiguration
{
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
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Groudeal.Identity.v1");
        var keyPath = c["DataProtection:KeyPath"];
        if (production) {
            if (!string.IsNullOrEmpty(c["Demo:Password"]) || !string.IsNullOrEmpty(c["Demo:AdminAuthenticatorKey"])) throw new InvalidOperationException("Development fixture configuration must be absent in Production.");
            if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath) || !Directory.Exists(keyPath)) throw new InvalidOperationException("Production requires an existing persistent DataProtection__KeyPath.");
            var certificatePath = c["DataProtection:CertificatePath"];
            if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath)) throw new InvalidOperationException("Production requires a Data Protection encryption certificate.");
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, c["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
            if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow) throw new InvalidOperationException("The current Data Protection certificate must be valid and include its private key.");
            dataProtection.ProtectKeysWithCertificate(cert);
            var decrypt = new List<X509Certificate2> { cert };
            foreach (var old in c.GetSection("DataProtection:PreviousCertificates").GetChildren()) {
                var previous = X509CertificateLoader.LoadPkcs12FromFile(old["Path"]!, old["Password"], X509KeyStorageFlags.EphemeralKeySet);
                if (!previous.HasPrivateKey) throw new InvalidOperationException("Previous key-decryption certificates need private keys."); decrypt.Add(previous);
            }
            dataProtection.UnprotectKeysWithAnyCertificate(decrypt.ToArray());
            var connection = new NpgsqlConnectionStringBuilder(c.GetConnectionString("Database"));
            if (connection.SslMode != SslMode.VerifyFull) throw new InvalidOperationException("Production PostgreSQL must use SslMode=VerifyFull.");
            if (connection.IncludeErrorDetail) throw new InvalidOperationException("Production database error detail must remain disabled.");
            if (!connection.Pooling || connection.MaxPoolSize is < 1 or > 200 || connection.Timeout is < 1 or > 30 || connection.CommandTimeout is < 1 or > 60) throw new InvalidOperationException("Production database pooling/timeouts are outside supported bounds.");
            if (string.IsNullOrWhiteSpace(c["AllowedHosts"]) || c["AllowedHosts"]!.Contains('*')) throw new InvalidOperationException("Production requires explicit AllowedHosts.");
            if (c.GetValue("AdminProvisioning:Enabled", false) && c["admin-operation"] is null) throw new InvalidOperationException("Provisioning permission must not remain enabled on the API host.");
        }
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath ?? Path.Combine(builder.Environment.ContentRootPath, ".local", "data-protection")));
        var attempts = c.GetValue("Operations:WorkerMaxAttempts", 10);
        if (attempts is < 1 or > 100) throw new InvalidOperationException("WorkerMaxAttempts must be between 1 and 100.");
    }
}


