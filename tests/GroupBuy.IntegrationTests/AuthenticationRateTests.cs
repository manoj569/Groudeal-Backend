using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    [Fact, Trait("Sprint5", "Security")]
    public async Task Profile_reads_do_not_exhaust_credential_budget_but_login_limit_remains_enforced()
    {
        using var f = new ApiFactory(fixture.Connection); using var client = f.CreateClient(); var user = await Register(client, "auth-budget"); await Login(client, user);
        for (var i = 0; i < 15; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        var request = new LoginRequest(user.Email!, "Strong!Password123");
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", request)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/login", request)).StatusCode);
    }
}
