using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Configurations;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GroupBuy.IntegrationTests;

public sealed class RenderTlsFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private readonly string directory=Path.Combine(DatabaseFixture.FindRepositoryRoot(),".local","render-tls-"+Guid.NewGuid().ToString("N"));
    public string Connection { get; private set; }="";
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        using var caKey=RSA.Create(2048);var caRequest=new CertificateRequest("CN=Synthetic disposable PostgreSQL CA",caKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
        using var ca=caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",serverKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");names.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},false));
        using var publicServer=request.Create(ca,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));
        var root=Path.Combine(directory,"synthetic-ca.pem");await File.WriteAllTextAsync(root,ca.ExportCertificatePem());
        container=new PostgreSqlBuilder("postgres:17-alpine")
            .WithResourceMapping(Encoding.UTF8.GetBytes(publicServer.ExportCertificatePem()),"/tmp/render-server.crt",70,70,UnixFileModes.UserRead)
            .WithResourceMapping(Encoding.UTF8.GetBytes(serverKey.ExportPkcs8PrivateKeyPem()),"/tmp/render-server.key",70,70,UnixFileModes.UserRead)
            .WithCommand(new OverwriteEnumerable<string>(["postgres","-c","ssl=on","-c","ssl_cert_file=/tmp/render-server.crt","-c","ssl_key_file=/tmp/render-server.key"]))
            .Build();
        await container.StartAsync();
        var c=new NpgsqlConnectionStringBuilder(container.GetConnectionString()){SslMode=SslMode.VerifyFull,RootCertificate=root,Pooling=true,MaxPoolSize=20,Timeout=15,CommandTimeout=30,IncludeErrorDetail=false};
        DatabaseFixture.ValidateAdminConnectionIsolated(c.ConnectionString);
        Connection=c.ConnectionString;
        await using var db=Db();await db.Database.MigrateAsync();
    }
    public MarketplaceDb Db(string? connection=null)=>new(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection??Connection).Options);
    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if(container!=null)await container.DisposeAsync();
        var resolved=Path.GetFullPath(directory);var allowed=Path.GetFullPath(Path.Combine(DatabaseFixture.FindRepositoryRoot(),".local"))+Path.DirectorySeparatorChar;
        if(!resolved.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refusing cleanup outside isolated test workspace.");
        if(Directory.Exists(resolved))Directory.Delete(resolved,true);
    }
}
