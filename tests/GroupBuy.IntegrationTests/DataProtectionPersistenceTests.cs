using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using System.Diagnostics;
using System.Net.Sockets;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public sealed class DataProtectionPersistenceTests(RenderTlsFixture fixture, Xunit.Abstractions.ITestOutputHelper testOutput) : IClassFixture<RenderTlsFixture>
{
    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="synthetic";
        public string ContentRootPath{get;set;}=DatabaseFixture.FindRepositoryRoot();public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
    }
    private static (string Encoded,string Password) Certificate()
    {
        using var rsa=RSA.Create(2048);var r=new CertificateRequest("CN=Synthetic persistence test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var c=r.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return(Convert.ToBase64String(c.Export(X509ContentType.Pfx,password)),password);
    }
    private ServiceProvider Provider(string connection,(string Encoded,string Password) certificate,(string Encoded,string Password)? previous=null)
    {
        var values=new Dictionary<string,string?>{["DataProtection:Persistence"]="Postgres",["DataProtection:CertificateBase64"]=certificate.Encoded,["DataProtection:CertificatePassword"]=certificate.Password};
        if(previous.HasValue){values["DataProtection:PreviousCertificates:0:Base64"]=previous.Value.Encoded;values["DataProtection:PreviousCertificates:0:Password"]=previous.Value.Password;}
        var services=new ServiceCollection();services.AddLogging();services.AddDbContext<MarketplaceDb>(o=>o.UseNpgsql(connection));
        DataProtectionSetup.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(values).Build(),new ProductionEnvironment());
        return services.BuildServiceProvider();
    }
    private async Task<string> EmptyDatabase()
    {
        var name="render_dp_"+Guid.NewGuid().ToString("N");await using var c=new NpgsqlConnection(fixture.Connection);await c.OpenAsync();
        await using var command=new NpgsqlCommand($"CREATE DATABASE {name}",c);await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(fixture.Connection){Database=name}.ConnectionString;
    }
    private async Task Drop(string connection)
    {
        var name=new NpgsqlConnectionStringBuilder(connection).Database!;if(!name.StartsWith("render_dp_",StringComparison.Ordinal))throw new InvalidOperationException("Not an owned disposable database.");
        NpgsqlConnection.ClearAllPools();await using var c=new NpgsqlConnection(fixture.Connection);await c.OpenAsync();await using var command=new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)",c);await command.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task Postgres_encrypted_ring_survives_destroyed_instances_and_certificate_rotation()
    {
        var connection=await EmptyDatabase();var old=Certificate();var current=Certificate();
        try
        {
            await using(var db=fixture.Db(connection))await db.Database.MigrateAsync();
            string protectedValue;
            using(var a=Provider(connection,old))protectedValue=a.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Protect("synthetic-factor");
            await using(var db=fixture.Db(connection))
            {
                var rows=await db.DataProtectionKeys.ToListAsync();Assert.NotEmpty(rows);
                foreach(var row in rows){Assert.NotNull(row.Xml);Assert.Contains(XElement.Parse(row.Xml!).Descendants(),x=>x.Name.LocalName=="encryptedSecret");Assert.DoesNotContain("synthetic-factor",row.Xml!);}
            }
            using(var b=Provider(connection,old))Assert.Equal("synthetic-factor",b.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Unprotect(protectedValue));
            using(var wrong=Provider(connection,current))Assert.Throws<CryptographicException>(()=>wrong.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Unprotect(protectedValue));
            using(var rotated=Provider(connection,current,old))
            {
                Assert.Equal("synthetic-factor",rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Unprotect(protectedValue));
                rotated.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(90));
            }
            using(var c=Provider(connection,current,old))Assert.Equal("synthetic-factor",c.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Unprotect(protectedValue));
        }finally{await Drop(connection);}
    }
    [Fact]
    public async Task Production_rejects_an_existing_unencrypted_ring_instead_of_silently_reusing_it()
    {
        var connection=await EmptyDatabase();
        try
        {
            await using(var db=fixture.Db(connection))await db.Database.MigrateAsync();
            var services=new ServiceCollection();services.AddLogging();services.AddDbContext<MarketplaceDb>(o=>o.UseNpgsql(connection));
            services.AddDataProtection().SetApplicationName("Groudeal.Identity.v1").PersistKeysToDbContext<MarketplaceDb>();
            using(var legacy=services.BuildServiceProvider())legacy.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-legacy").Protect("synthetic");
            using var production=Provider(connection,Certificate());
            Assert.Contains("Unencrypted",Assert.Throws<InvalidOperationException>(()=>DataProtectionSetup.ValidatePersistedKeyRing(production,true)).Message);
        }
        finally{await Drop(connection);}
    }
    [Fact]
    public async Task Migration_from_empty_isolated_Postgres_creates_the_official_key_table()
    {
        var connection=await EmptyDatabase();
        try
        {
            await using var db=fixture.Db(connection);await db.Database.MigrateAsync();
            Assert.Equal(db.Database.GetMigrations(),await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.DataProtectionKeys.ToListAsync());Assert.Empty(await db.Users.ToListAsync());Assert.Equal(3,await db.Roles.CountAsync());
        }
        finally{await Drop(connection);}
    }
    [Fact]
    public async Task Migration_from_previous_schema_preserves_accounts_profiles_and_consents()
    {
        var connection=await EmptyDatabase();var id=Guid.NewGuid();
        try
        {
            await using var db=fixture.Db(connection);await db.GetService<IMigrator>().MigrateAsync("20261106104909_CustomerPasswordlessAuthentication");
            db.Users.Add(new IdentityAccount{Id=id,UserName="synthetic-upgrade",NormalizedUserName="SYNTHETIC-UPGRADE"});
            db.UsersProfile.Add(new User{Id=id,DisplayName="Preserved customer"});
            db.UserConsents.Add(new UserConsent{UserId=id,DocumentType="TermsOfService",DocumentVersion="synthetic-v1",AcceptedAtUtc=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
            await db.Database.MigrateAsync();Assert.Equal("Preserved customer",(await db.UsersProfile.SingleAsync(x=>x.Id==id)).DisplayName);
            Assert.Equal("synthetic-v1",(await db.UserConsents.SingleAsync(x=>x.UserId==id)).DocumentVersion);Assert.Empty(await db.DataProtectionKeys.ToListAsync());
            Assert.Equal(db.Database.GetMigrations(),await db.Database.GetAppliedMigrationsAsync());
        }finally{await Drop(connection);}
    }
    [Fact]
    public async Task Runtime_role_can_read_append_keys_but_cannot_update_delete_or_migrate()
    {
        var connection=await EmptyDatabase();var role="render_runtime_"+Guid.NewGuid().ToString("N");var password=DatabaseFixture.CreateTestRuntimePassword();
        try
        {
            await using(var db=fixture.Db(connection))await db.Database.MigrateAsync();
            await using var owner=new NpgsqlConnection(connection);await owner.OpenAsync();await DatabaseFixture.CreateRuntimeLoginRoleAsync(owner,role,password);
            var sql=(await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.FindRepositoryRoot(),"scripts","grant-runtime.sql"))).Replace(":'app_role'","@role");
            await using(var grant=new NpgsqlCommand(sql,owner)){grant.Parameters.AddWithValue("role",role);await grant.ExecuteNonQueryAsync();}
            var runtime=new NpgsqlConnectionStringBuilder(connection){Username=role,Password=password}.ConnectionString;var cert=Certificate();string value;
            using(var a=Provider(runtime,cert))value=a.GetRequiredService<IDataProtectionProvider>().CreateProtector("runtime").Protect("synthetic-private-factor");
            using(var b=Provider(runtime,cert))Assert.Equal("synthetic-private-factor",b.GetRequiredService<IDataProtectionProvider>().CreateProtector("runtime").Unprotect(value));
            await using var limited=new NpgsqlConnection(runtime);await limited.OpenAsync();
            foreach(var statement in new[]{"UPDATE data_protection_keys SET \"Xml\"=\"Xml\" WHERE false","DELETE FROM data_protection_keys WHERE false","CREATE TABLE forbidden_render_ddl(id int)"})
            {await using var cmd=new NpgsqlCommand(statement,limited);Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync())).SqlState);}
        }finally
        {
            await using(var owner=new NpgsqlConnection(connection)){await owner.OpenAsync();await using var cleanup=new NpgsqlCommand($"DROP OWNED BY {role}; DROP ROLE {role};",owner);await cleanup.ExecuteNonQueryAsync();}
            await Drop(connection);
        }
    }
    [Fact]
    public async Task Production_postgres_startup_survives_restart_without_key_path_and_rejects_wrong_certificate_and_outage()
    {
        var certificate=Certificate();var wrong=Certificate();
        var keys=new Dictionary<string,string?>{["ConnectionStrings__Database"]=fixture.Connection,["DataProtection__Persistence"]="Postgres",["DataProtection__KeyPath"]=null,
            ["DataProtection__CertificatePath"]=null,["DataProtection__CertificateBase64"]=certificate.Encoded,["DataProtection__CertificatePassword"]=certificate.Password,
            ["AllowedHosts"]="localhost",["Demo__Password"]=null,["Demo__AdminAuthenticatorKey"]=null};
        var before=keys.Keys.ToDictionary(k=>k,Environment.GetEnvironmentVariable);
        try
        {
            foreach(var entry in keys)Environment.SetEnvironmentVariable(entry.Key,entry.Value);
            var overrides=keys.ToDictionary(x=>x.Key.Replace("__",":"),x=>x.Value);overrides["Demo:Password"]=null;
            string value;
            using(var a=new ApiFactory(fixture.Connection,"Production",overrides,disableWorker:true))
            {using var client=a.CreateClient();Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/health/live")).StatusCode);value=a.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("production-restart").Protect("synthetic-factor");}
            using(var b=new ApiFactory(fixture.Connection,"Production",overrides,disableWorker:true))
            {using var client=b.CreateClient();Assert.Equal("synthetic-factor",b.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("production-restart").Unprotect(value));}
            Environment.SetEnvironmentVariable("DataProtection__CertificateBase64",wrong.Encoded);Environment.SetEnvironmentVariable("DataProtection__CertificatePassword",wrong.Password);
            overrides["DataProtection:CertificateBase64"]=wrong.Encoded;overrides["DataProtection:CertificatePassword"]=wrong.Password;
            using(var invalid=new ApiFactory(fixture.Connection,"Production",overrides,disableWorker:true)){var ex=Assert.Throws<InvalidOperationException>(()=>invalid.CreateClient());Assert.Null(ex.InnerException);Assert.DoesNotContain(wrong.Encoded,ex.Message);}
            Environment.SetEnvironmentVariable("DataProtection__CertificateBase64",certificate.Encoded);Environment.SetEnvironmentVariable("DataProtection__CertificatePassword",certificate.Password);
            var unavailable=new NpgsqlConnectionStringBuilder(fixture.Connection){Port=1,Timeout=1,CommandTimeout=1}.ConnectionString;Environment.SetEnvironmentVariable("ConnectionStrings__Database",unavailable);
            overrides["DataProtection:CertificateBase64"]=certificate.Encoded;overrides["DataProtection:CertificatePassword"]=certificate.Password;overrides["ConnectionStrings:Database"]=unavailable;
            using(var down=new ApiFactory(unavailable,"Production",overrides,disableWorker:true)){var ex=Assert.Throws<InvalidOperationException>(()=>down.CreateClient());Assert.Null(ex.InnerException);Assert.DoesNotContain("Host=",ex.Message);}
        }finally{foreach(var entry in before)Environment.SetEnvironmentVariable(entry.Key,entry.Value);}
    }
    [Fact]
    public async Task Real_production_Kestrel_binds_Render_PORT_and_ready_probe_without_a_persistent_disk()
    {
        var connection=await EmptyDatabase();Process? process=null;
        try
        {
            await using(var db=fixture.Db(connection))await db.Database.MigrateAsync();
            var cert=Certificate();var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            Assert.NotEqual(55432,port);
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=DatabaseFixture.FindRepositoryRoot()};
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("DataProtection__",StringComparison.OrdinalIgnoreCase)||k.StartsWith("Demo__",StringComparison.OrdinalIgnoreCase)||k.StartsWith("Proxy__",StringComparison.OrdinalIgnoreCase)||k.StartsWith("Authentication__",StringComparison.OrdinalIgnoreCase)||k.StartsWith("Payments__",StringComparison.OrdinalIgnoreCase)||k.StartsWith("Pilot__",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
            foreach(var entry in new Dictionary<string,string>{["ASPNETCORE_ENVIRONMENT"]="Production",["DOTNET_ENVIRONMENT"]="Production",["PORT"]=port.ToString(),["AllowedHosts"]="127.0.0.1",["ConnectionStrings__Database"]=connection,
                ["Jwt__SigningKey"]=Convert.ToHexString(RandomNumberGenerator.GetBytes(64)),["Jwt__Issuer"]="synthetic-render",["Jwt__Audience"]="synthetic-render-client",["DataProtection__Persistence"]="Postgres",["DataProtection__CertificateBase64"]=cert.Encoded,["DataProtection__CertificatePassword"]=cert.Password,
                ["Pilot__Enabled"]="false",["Payments__Enabled"]="false",["Payments__SandboxEnabled"]="false",["Authentication__Google__Enabled"]="false",["Authentication__EmailOtp__Enabled"]="false",["Authentication__MobileOtp__Enabled"]="false",["ASPNETCORE_FORWARDEDHEADERS_ENABLED"]="false"})start.Environment[entry.Key]=entry.Value;
            process=Process.Start(start)??throw new InvalidOperationException("Synthetic API did not start.");var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(3)};
            var deadline=DateTimeOffset.UtcNow.AddSeconds(30);var ready=false;var lastStatus="no-response";var startupTimeouts=0;var refused=0;
            while(DateTimeOffset.UtcNow<deadline&&!process.HasExited)
            {
                try{var response=await client.GetAsync("/health/ready");lastStatus=((int)response.StatusCode).ToString();ready=response.StatusCode==HttpStatusCode.OK;if(ready)break;}
                catch(HttpRequestException){refused++;}
                catch(TaskCanceledException){startupTimeouts++;lastStatus="startup-probe-timeout";}
                await Task.Delay(100);
            }
            testOutput.WriteLine("Startup readiness polling: {0} unavailable-connection probes, {1} timed-out probes; fixed deadline=30s, request timeout=3s.",refused,startupTimeouts);
            if(!ready)
            {
                if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();
                var output=await stdout;var errors=await stderr;
                var types=new List<string>();
                foreach(var line in output.Split('\n'))
                {
                    try{using var json=System.Text.Json.JsonDocument.Parse(line);if(json.RootElement.TryGetProperty("Properties",out var properties)&&properties.TryGetProperty("ErrorType",out var error))types.Add(error.GetString()??"unknown");}catch(System.Text.Json.JsonException){}
                }
                var unhandled=System.Text.RegularExpressions.Regex.Match(errors,@"Unhandled exception\. ([A-Za-z0-9_.]+)");
                Assert.True(ready,$"Production API readiness failed; last status={lastStatus}; exit={process.ExitCode}; safe error types={string.Join(",",types)}; unhandled={unhandled.Groups[1].Value}.");
            }
            using var spoof=new HttpRequestMessage(HttpMethod.Get,"/api/v1/me");spoof.Headers.Add("X-Forwarded-Proto","https");spoof.Headers.Add("X-Forwarded-For","203.0.113.17");
            Assert.Equal(HttpStatusCode.TemporaryRedirect,(await client.SendAsync(spoof)).StatusCode);
            var probe=new ProcessStartInfo(start.FileName){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};probe.ArgumentList.Add(typeof(Program).Assembly.Location);probe.ArgumentList.Add("--healthcheck");
            foreach(var entry in start.Environment)probe.Environment[entry.Key]=entry.Value;
            using var health=Process.Start(probe)!;await health.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(0,health.ExitCode);
            process.Kill(entireProcessTree:true);await process.WaitForExitAsync();await stdout;await stderr;
        }
        finally
        {
            if(process!=null){if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}process.Dispose();}
            await Drop(connection);
        }
    }
}
