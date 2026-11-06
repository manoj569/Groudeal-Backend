using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace GroupBuy.Infrastructure;

public static class DataProtectionSetup
{
    public static void ValidatePersistedKeyRing(IServiceProvider services, bool requireEncryption)
    {
        if (requireEncryption)
        {
            var repository = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
                ?? throw new InvalidOperationException("Persistent encrypted Data Protection storage is required.");
            foreach (var key in repository.GetAllElements().Where(e => e.Name.LocalName == "key"))
                if (!key.Descendants().Any(e => e.Name.LocalName == "encryptedSecret") || key.Descendants().Any(e => e.Name.LocalName == "masterKey"))
                    throw new InvalidOperationException("Unencrypted Data Protection key material is forbidden in Production.");
        }
        foreach (var key in services.GetRequiredService<IKeyManager>().GetAllKeys().Where(k => !k.IsRevoked)) _ = key.CreateEncryptor();
    }
    public static void Configure(IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var production = !environment.IsDevelopment();
        var mode = config["DataProtection:Persistence"] ?? "FileSystem";
        if (mode is not ("FileSystem" or "Postgres")) throw new InvalidOperationException("DataProtection__Persistence must be FileSystem or Postgres.");
        var builder = services.AddDataProtection().SetApplicationName("Groudeal.Identity.v1");
        if (mode == "Postgres") builder.PersistKeysToDbContext<MarketplaceDb>();
        else
        {
            var path = config["DataProtection:KeyPath"];
            if (production && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)))
                throw new InvalidOperationException("FileSystem production persistence requires an existing persistent DataProtection__KeyPath.");
            builder.PersistKeysToFileSystem(new DirectoryInfo(path ?? Path.Combine(environment.ContentRootPath, ".local", "data-protection")));
        }
        if (production || HasCertificate(config.GetSection("DataProtection")))
        {
            var current = LoadCertificate(config.GetSection("DataProtection"), current: true);
            builder.ProtectKeysWithCertificate(current);
            var certificates = new List<X509Certificate2> { current };
            foreach (var previous in config.GetSection("DataProtection:PreviousCertificates").GetChildren())
                certificates.Add(LoadCertificate(previous, current: false));
            builder.UnprotectKeysWithAnyCertificate(certificates.ToArray());
        }
    }
    private static bool HasCertificate(IConfiguration c) => !string.IsNullOrWhiteSpace(c["CertificatePath"]) || !string.IsNullOrWhiteSpace(c["CertificateBase64"]);
    public static X509Certificate2 LoadCertificate(IConfiguration c, bool current)
    {
        var path = c[current ? "CertificatePath" : "Path"];
        var encoded = c[current ? "CertificateBase64" : "Base64"];
        var password = c[current ? "CertificatePassword" : "Password"];
        if (string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("Configure exactly one Data Protection certificate source: path or base64 PKCS#12.");
        X509Certificate2? cert = null;
        try
        {
            cert = !string.IsNullOrWhiteSpace(encoded)
                ? X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(encoded), password, X509KeyStorageFlags.EphemeralKeySet)
                : X509CertificateLoader.LoadPkcs12FromFile(path!, password, X509KeyStorageFlags.EphemeralKeySet);
            if (!cert.HasPrivateKey || (current && (cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow)))
                throw new CryptographicException();
            using var key = cert.GetRSAPrivateKey();
            if (key is null || key.KeySize < 2048) throw new CryptographicException();
            return cert;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            cert?.Dispose();
            // Never propagate loader errors containing paths, encoded material or passwords.
            throw new InvalidOperationException("Data Protection certificate could not be loaded or validated.");
        }
    }
}
