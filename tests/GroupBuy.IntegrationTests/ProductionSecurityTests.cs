using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact, Trait("Sprint5", "Security")]
    public async Task A_validly_signed_Admin_JWT_without_MFA_is_rejected_even_after_enrollment()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var x = await StartEnrollment(f, client);
        var full = await CompleteMfa(client, x.User, x.Challenge); var original = new JwtSecurityTokenHandler().ReadJwtToken(full.AccessToken);
        var config = f.Services.GetRequiredService<IConfiguration>();
        var claims = original.Claims.Where(c => c.Type is not ("amr" or "exp" or "nbf" or "iss" or "aud")).ToList();
        var forgedContext = new JwtSecurityToken(config["Jwt:Issuer"], config["Jwt:Audience"], claims, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(1),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(forgedContext));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/audit-logs")).StatusCode);
    }
    [Fact, Trait("Sprint5", "Security")]
    public async Task Production_requires_encrypted_key_storage_verified_database_TLS_and_redirects_API_HTTP()
    {
        using (var missing = new ApiFactory(fixture.Connection, "Production"))
        {
            Assert.Throws<InvalidOperationException>(() => missing.CreateClient());
        }

        var path = Path.Combine(DatabaseFixture.FindRepositoryRoot(), ".local", "production-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var keyPath = Path.Combine(path, "keys");
            Directory.CreateDirectory(keyPath);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Groudeal Synthetic Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificatePath = Path.Combine(path, "synthetic.pfx");
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(certificatePath, cert.Export(X509ContentType.Pfx, password));
            var settings = new Dictionary<string, string?>
            {
                ["DataProtection:KeyPath"] = keyPath,
                ["DataProtection:CertificatePath"] = certificatePath,
                ["DataProtection:CertificatePassword"] = password,
                ["AllowedHosts"] = "localhost",
                ["Demo:Password"] = null
            };
            var secure = new Npgsql.NpgsqlConnectionStringBuilder(fixture.Connection) { SslMode = Npgsql.SslMode.VerifyFull };
            var environment = new Dictionary<string, string?>
            {
                ["DataProtection__KeyPath"] = keyPath,
                ["DataProtection__CertificatePath"] = certificatePath,
                ["DataProtection__CertificatePassword"] = password,
                ["AllowedHosts"] = "localhost",
                ["ConnectionStrings__Database"] = fixture.Connection,
                ["Demo__Password"] = null,
                ["Demo__AdminAuthenticatorKey"] = null
            };
            var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            try
            {
                // Minimal-host production guards run before WebApplicationFactory's late app-config hook.
                foreach (var item in environment)
                {
                    Environment.SetEnvironmentVariable(item.Key, item.Value);
                }

                using (var insecure = new ApiFactory(fixture.Connection, "Production", settings))
                {
                    var error = Assert.Throws<InvalidOperationException>(() => insecure.CreateClient());
                    Assert.Contains("VerifyFull", error.Message);
                }

                Environment.SetEnvironmentVariable("ConnectionStrings__Database", secure.ConnectionString);
                using var factory = new ApiFactory(secure.ConnectionString, "Production", settings);
                using var client = ((WebApplicationFactory<Program>)factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

                var worker = factory.Services.GetRequiredService<WorkerHealthState>();
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (worker.Read().LastBatchErrorType is null && wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(25);
                }

                Assert.Equal("NpgsqlException", worker.Read().LastBatchErrorType);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
                var redirect = await client.GetAsync("/api/v1/auth/me");
                Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
                Assert.Equal("https", redirect.Headers.Location!.Scheme);

                var protector = Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.CreateProtector(
                    factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
                    "sprint5-synthetic-proof");
                var protectedValue = Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(protector, "synthetic-value");
                Assert.Equal("synthetic-value", Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(protector, protectedValue));
                var files = Directory.GetFiles(keyPath, "key-*.xml");
                Assert.NotEmpty(files);
                foreach (var file in files)
                {
                    if (!System.Xml.Linq.XDocument.Load(file).Descendants().Any(e => e.Name.LocalName == "encryptedSecret"))
                    {
                        Assert.Fail("Production Data Protection master keys were not encrypted.");
                    }
                }
            }
            finally
            {
                foreach (var item in previous)
                {
                    Environment.SetEnvironmentVariable(item.Key, item.Value);
                }
            }

            // Certificate is synthetic and PostgreSQL fixture does not serve TLS: no production TLS success is claimed.
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
    [Fact, Trait("Sprint5", "Security")]
    public async Task Worker_exhaustion_is_visible_stops_automatic_attempts_and_resumes_only_after_reviewed_reset()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        f.Services.GetRequiredService<IConfiguration>()["Operations:WorkerMaxAttempts"] = "2";
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "exhausted-worker"))); await SetDeadline(f, x.Campaign, true);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION sprint5_worker_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Action\" = 'ConfirmationExpired' THEN RAISE EXCEPTION 'synthetic failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER sprint5_worker_failure BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION sprint5_worker_failure();");
        }
        try
        {
            for (var i = 0; i < 2; i++)
            {
                await using (var scope = f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkerRetryAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
                await Tick(f);
            }
            await Tick(f); var stored = await StoredCampaign(f, x.Campaign); Assert.Equal(2, stored.WorkerFailureCount); Assert.Equal(CampaignStatus.AwaitingSellerConfirmation, stored.Status);
            var health = await Read<OperationsHealthResponse>(await admin.GetAsync("/api/v1/admin/operations/health")); Assert.True(health.ExhaustedCampaigns >= 1);
        }
        finally
        {
            await using var scope = f.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Database.ExecuteSqlRawAsync("DROP TRIGGER sprint5_worker_failure ON audit_logs; DROP FUNCTION sprint5_worker_failure();");
        }
        await Tick(f); Assert.Equal(2, (await StoredCampaign(f, x.Campaign)).WorkerFailureCount);
        // Simulate the explicitly reviewed operator reset only in the disposable fixture.
        await using (var scope = f.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkerFailureCount, 0).SetProperty(c => c.WorkerRetryAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await Tick(f); await Tick(f); Assert.Equal(CampaignStatus.Failed, (await StoredCampaign(f, x.Campaign)).Status);
        await using var verify = f.Services.CreateAsyncScope(); Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<MarketplaceDb>().AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "ConfirmationExpired"));
    }
}
