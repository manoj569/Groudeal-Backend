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
    private string? nativeBin;
    private bool nativeStarted;
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
        nativeBin=Environment.GetEnvironmentVariable("GROUPBUY_TEST_PG_BIN");
        string connection;
        if(nativeBin is not null)
        {
            var cert=Path.Combine(directory,"server.crt");var key=Path.Combine(directory,"server.key");
            await File.WriteAllTextAsync(cert,publicServer.ExportCertificatePem());
            await File.WriteAllTextAsync(key,serverKey.ExportPkcs8PrivateKeyPem());
            var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var passwordFile=Path.Combine(directory,"password.txt");
            await File.WriteAllTextAsync(passwordFile,password);
            var data=Path.Combine(directory,"data");
            try { await NativeTool("initdb","-D",data,"-U","tls_fixture","--pwfile="+passwordFile,"-A","scram-sha-256","-E","UTF8","--locale=C"); }
            finally { File.Delete(passwordFile); }
            var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            if(port==55432)throw new InvalidOperationException("Protected endpoint is excluded from TLS fixtures.");
            static string ConfigPath(string path)=>path.Replace('\\','/').Replace("'","''");
            await File.AppendAllTextAsync(Path.Combine(data,"postgresql.conf"),$"\nlisten_addresses = '127.0.0.1'\nport = {port}\nssl = on\nssl_cert_file = '{ConfigPath(cert)}'\nssl_key_file = '{ConfigPath(key)}'\n");
            try { await NativeTool("pg_ctl","-D",data,"-l",Path.Combine(directory,"postgres.log"),"-w","start"); }
            finally { nativeStarted=File.Exists(Path.Combine(data,"postmaster.pid")); }
            connection=new NpgsqlConnectionStringBuilder { Host="127.0.0.1",Port=port,Database="postgres",Username="tls_fixture",Password=password }.ConnectionString;
        }
        else
        {
            container=new PostgreSqlBuilder("postgres:17-alpine")
            .WithResourceMapping(Encoding.UTF8.GetBytes(publicServer.ExportCertificatePem()),"/tmp/render-server.crt",70,70,UnixFileModes.UserRead)
            .WithResourceMapping(Encoding.UTF8.GetBytes(serverKey.ExportPkcs8PrivateKeyPem()),"/tmp/render-server.key",70,70,UnixFileModes.UserRead)
            .WithCommand(new OverwriteEnumerable<string>(["postgres","-c","ssl=on","-c","ssl_cert_file=/tmp/render-server.crt","-c","ssl_key_file=/tmp/render-server.key"]))
            .Build();
            await container.StartAsync();
            connection=container.GetConnectionString();
        }
        var c=new NpgsqlConnectionStringBuilder(connection){SslMode=SslMode.VerifyFull,RootCertificate=root,Pooling=true,MaxPoolSize=20,Timeout=15,CommandTimeout=30,IncludeErrorDetail=false};
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
        if(nativeStarted){await NativeTool("pg_ctl","-D",Path.Combine(resolved,"data"),"-m","fast","-w","stop");nativeStarted=false;}
        if(Directory.Exists(resolved))Directory.Delete(resolved,true);
    }

    private async Task NativeTool(string tool,params string[] arguments)
    {
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(nativeBin!,tool+(OperatingSystem.IsWindows()?".exe":""))) {
            UseShellExecute=false,CreateNoWindow=true,
            // pg_ctl's background server can retain inherited pipe handles. Wait
            // only for its parent process; the server writes to the owned log.
            RedirectStandardOutput=tool!="pg_ctl",RedirectStandardError=tool!="pg_ctl" };
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=System.Diagnostics.Process.Start(start)!;
        var output=start.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var error=start.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch(OperationCanceledException){process.Kill(entireProcessTree:true);throw;}
        await File.WriteAllTextAsync(Path.Combine(directory,tool+"-out.log"),await output);
        await File.WriteAllTextAsync(Path.Combine(directory,tool+"-error.log"),await error);
        if(process.ExitCode!=0)throw new InvalidOperationException("Disposable native TLS PostgreSQL control failed; inspect its owned fixture logs.");
    }
}
