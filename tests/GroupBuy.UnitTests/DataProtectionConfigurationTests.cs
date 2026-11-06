using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.UnitTests;

public sealed class DataProtectionConfigurationTests
{
    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static IConfiguration Config(Dictionary<string,string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static string Certificate(string fault, string password)
    {
        using var key = RSA.Create(fault == "weak" ? 1024 : 2048);
        var request = new CertificateRequest("CN=Synthetic unit certificate", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(fault == "future" ? 1 : -2), DateTimeOffset.UtcNow.AddDays(fault == "expired" ? -1 : 2));
        return Convert.ToBase64String(fault == "public" ? cert.Export(X509ContentType.Cert) : cert.Export(X509ContentType.Pfx, password));
    }
    [Fact]
    public void Postgres_selects_official_repository_and_certificate_encryption_without_any_key_directory()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var config = Config(new() { ["DataProtection:Persistence"]="Postgres", ["DataProtection:CertificateBase64"]=Certificate("valid",password), ["DataProtection:CertificatePassword"]=password });
        var services = new ServiceCollection(); services.AddLogging(); services.AddDbContext<MarketplaceDb>(o => o.UseNpgsql("Host=unused;Database=unused"));
        DataProtectionSetup.Configure(services,config,new EnvironmentStub());
        using var provider = services.BuildServiceProvider(); var options = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        Assert.Contains("EntityFrameworkCoreXmlRepository",options.XmlRepository!.GetType().Name);
        Assert.Contains("CertificateXmlEncryptor",options.XmlEncryptor!.GetType().Name);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("public")]
    [InlineData("weak")]
    [InlineData("password")]
    public void Invalid_production_certificates_fail_closed_without_secret_diagnostics(string fault)
    {
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var encoded = fault == "missing" ? null : fault == "malformed" ? "synthetic-invalid-base64!" : Certificate(fault,password);
        var config=Config(new(){ ["DataProtection:Persistence"]="Postgres", ["DataProtection:CertificateBase64"]=encoded, ["DataProtection:CertificatePassword"]=fault=="password" ? password+"wrong" : password });
        var error=Assert.Throws<InvalidOperationException>(()=>DataProtectionSetup.Configure(new ServiceCollection(),config,new EnvironmentStub()));
        Assert.Null(error.InnerException); Assert.DoesNotContain(password,error.Message); if(encoded!=null)Assert.DoesNotContain(encoded,error.Message);
    }
    [Theory]
    [InlineData("disk")]
    [InlineData("postgres")]
    [InlineData("")]
    public void Unsupported_modes_never_select_a_fallback(string mode) => Assert.Throws<InvalidOperationException>(() =>
        DataProtectionSetup.Configure(new ServiceCollection(),Config(new(){["DataProtection:Persistence"]=mode}),new EnvironmentStub()));
    [Fact]
    public void Legacy_file_mode_still_requires_a_persistent_absolute_directory() => Assert.Throws<InvalidOperationException>(()=>
        DataProtectionSetup.Configure(new ServiceCollection(),Config(new(){["DataProtection:KeyPath"]="relative-keys"}),new EnvironmentStub()));
    [Fact]
    public void Old_customer_profile_payload_deserializes_with_the_existing_properties()
    {
        var value=JsonSerializer.Deserialize<CustomerProfile>("""{"id":"00000000-0000-0000-0000-000000000001","displayName":"Customer","profileCompleted":true,"emailVerified":true,"phoneVerified":false,"consentRequired":true,"termsVersion":null,"privacyVersion":null}""",new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(value!.EmailVerified);Assert.Null(value.Email);Assert.Null(value.ConnectedMethods);
    }
}
