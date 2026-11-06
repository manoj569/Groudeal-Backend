using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GroupBuy.API;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.IntegrationTests;

public sealed class RenderProductionConfigurationTests
{
    private static WebApplicationBuilder Builder(Dictionary<string,string?>? overrides=null)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Production",ContentRootPath=DatabaseFixture.FindRepositoryRoot()});
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=Synthetic config test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        builder.Configuration.Sources.Clear();builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
            ["DataProtection:Persistence"]="Postgres",["DataProtection:CertificateBase64"]=Convert.ToBase64String(cert.Export(X509ContentType.Pfx,password)),["DataProtection:CertificatePassword"]=password,
            ["ConnectionStrings:Database"]="Host=unused;Database=synthetic;SSL Mode=VerifyFull;Pooling=true;Maximum Pool Size=20;Timeout=15;Command Timeout=30;Include Error Detail=false",["AllowedHosts"]="api.example.invalid"
        }.Concat(overrides??new()).GroupBy(x=>x.Key).Select(g=>g.Last()));
        return builder;
    }
    [Fact]
    public void Render_postgres_configuration_needs_no_persistent_directory_and_uses_PORT()
    {
        var b=Builder(new(){["PORT"]="10000"});ProductionConfiguration.ConfigurePort(b);ProductionConfiguration.Configure(b);
        Assert.Equal("http://0.0.0.0:10000",b.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey));
    }
    [Theory]
    [InlineData("PORT","80")]
    [InlineData("PORT","invalid")]
    [InlineData("PORT","65536")]
    [InlineData("AllowedHosts","*")]
    [InlineData("AllowedHosts","https://api.example.invalid")]
    [InlineData("ASPNETCORE_FORWARDEDHEADERS_ENABLED","true")]
    [InlineData("ConnectionStrings:Database","Host=unused;SSL Mode=Require")]
    [InlineData("ConnectionStrings:Database","Host=unused;SSL Mode=VerifyFull;Include Error Detail=true")]
    [InlineData("ConnectionStrings:Database","Host=unused;SSL Mode=VerifyFull;Pooling=false")]
    [InlineData("ConnectionStrings:Database","Host=unused;SSL Mode=VerifyFull;Maximum Pool Size=201")]
    public void Insecure_production_configuration_fails_closed(string key,string value)
    {
        var b=Builder(new(){[key]=value});Assert.Throws<InvalidOperationException>(()=>{ProductionConfiguration.ConfigurePort(b);ProductionConfiguration.Configure(b);});
    }
    [Fact]
    public async Task Production_customer_activation_requires_approved_legal_versions()
    {
        var b=Builder(new(){["Authentication:Google:Enabled"]="true",["Authentication:Google:ClientId"]="synthetic-client"});
        await using var app=b.Build();
        Assert.Contains("approved Terms",Assert.Throws<InvalidOperationException>(()=>app.ValidateCustomerAuthentication()).Message);
    }
    [Theory]
    [InlineData(false,true)]
    [InlineData(true,true)]
    [InlineData(true,false)]
    public async Task Forwarded_headers_require_the_configured_peer_and_symmetric_single_hop(bool trusted,bool symmetric)
    {
        var path=Path.Combine(DatabaseFixture.FindRepositoryRoot(),".local","proxy-proof-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        try
        {
            var b=Builder(new(){["DataProtection:Persistence"]="FileSystem",["DataProtection:KeyPath"]=path,["Proxy:KnownProxies:0"]="10.20.30.40",["AllowedHosts"]="localhost"});
            b.WebHost.UseTestServer();ProductionConfiguration.Configure(b);await using var app=b.Build();
            app.Use(async(c,next)=>{c.Connection.RemoteIpAddress=IPAddress.Parse(trusted?"10.20.30.40":"198.51.100.20");await next();});
            app.UseForwardedHeaders();app.Run(c=>c.Response.WriteAsJsonAsync(new{scheme=c.Request.Scheme,peer=c.Connection.RemoteIpAddress!.ToString()}));await app.StartAsync();
            using var client=app.GetTestClient();using var request=new HttpRequestMessage(HttpMethod.Get,"/");request.Headers.Add("X-Forwarded-For",symmetric?"203.0.113.17":"203.0.113.17, 192.0.2.2");request.Headers.Add("X-Forwarded-Proto","https");
            var result=JsonDocument.Parse(await (await client.SendAsync(request)).Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(trusted&&symmetric?"https":"http",result.GetProperty("scheme").GetString());
            Assert.Equal(trusted&&symmetric?"203.0.113.17":trusted?"10.20.30.40":"198.51.100.20",result.GetProperty("peer").GetString());
        }
        finally
        {
            var allowed=Path.GetFullPath(Path.Combine(DatabaseFixture.FindRepositoryRoot(),".local"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(path).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Refusing unsafe test cleanup.");
            if(Directory.Exists(path))Directory.Delete(path,true);
        }
    }
}
