using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.API;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    private sealed class FrozenAuthenticationWindow : AuthenticationRateLimitSettings
    {
        public override RateLimitPartition<string> Partition(string key) => RateLimitPartition.Get(key, _ => new FrozenLimiter(CreateOptions()));
        // Hide ReplenishingRateLimiter from the partition manager, which otherwise
        // calls TryReplenish even when AutoReplenishment is false. The real .NET
        // fixed-window implementation still owns every quota/lease decision.
        private sealed class FrozenLimiter : RateLimiter
        {
            private readonly FixedWindowRateLimiter inner;
            public FrozenLimiter(FixedWindowRateLimiterOptions options) { options.AutoReplenishment=false; inner=new(options); }
            public override TimeSpan? IdleDuration => null;
            public override RateLimiterStatistics? GetStatistics()=>inner.GetStatistics();
            protected override RateLimitLease AttemptAcquireCore(int permitCount)=>inner.AttemptAcquire(permitCount);
            protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount,CancellationToken ct)=>inner.AcquireAsync(permitCount,ct);
            protected override void Dispose(bool disposing) { if(disposing)inner.Dispose();base.Dispose(disposing); }
        }
    }
    [Fact, Trait("Sprint5", "Security")]
    public async Task Profile_reads_do_not_exhaust_credential_budget_but_login_limit_remains_enforced()
    {
        using var source = new ApiFactory(fixture.Connection);
        // Freeze replenishment, not the limit: registration + initial login +
        // eight logins consume exactly ten permits even on a slow full-suite run.
        using var f = source.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<AuthenticationRateLimitSettings>(new FrozenAuthenticationWindow())));
        using var client = f.CreateClient(); var user = await Register(client, "auth-budget"); await Login(client, user);
        for (var i = 0; i < 15; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var request = new LoginRequest(user.Email!, "Strong!Password123");
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", request)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/login", request)).StatusCode);
    }
}
