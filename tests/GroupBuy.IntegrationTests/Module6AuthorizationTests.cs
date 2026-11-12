using System.Net;
using GroupBuy.Application;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    [Fact]
    public async Task Module6_missing_order_long_search_and_seller_only_authorization()
    {
        using var f=new ApiFactory(fixture.Connection,disableWorker:true); using var customer=await CheckoutCustomer(f);
        var id=Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,(await customer.GetAsync($"/api/v1/me/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await customer.GetAsync("/api/v1/me/orders?search="+new string('x',101))).StatusCode);
        var me=await Read<UserResponse>(await customer.GetAsync("/api/v1/auth/me"));
        await using(var scope=f.Services.CreateAsyncScope()) {
            var users=scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            var user=await users.FindByIdAsync(me.Id.ToString());
            Assert.True((await users.RemoveFromRoleAsync(user!,"Customer")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user!,"Seller")).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized,(await customer.GetAsync("/api/v1/me/orders")).StatusCode);
        await using(var scope=f.Services.CreateAsyncScope()) {
            var token=await scope.ServiceProvider.GetRequiredService<IIdentityService>().Login(new(me.Email!,"Synthetic!Password123"),default);
            customer.DefaultRequestHeaders.Authorization=new("Bearer",token.AccessToken);
        }
        Assert.Equal(HttpStatusCode.Forbidden,(await customer.GetAsync("/api/v1/me/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await customer.GetAsync($"/api/v1/me/orders/{id}")).StatusCode);
    }
}
