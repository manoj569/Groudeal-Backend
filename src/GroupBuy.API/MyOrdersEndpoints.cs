using System.Security.Claims;
using GroupBuy.Application;
namespace GroupBuy.API;
public static class MyOrdersEndpoints
{
    private static Actor Customer(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!),false,c.TraceIdentifier);
    public static void MapMyOrders(this WebApplication app)
    {
        var customer=app.MapGroup("/api/v1/me/orders").RequireAuthorization("Customer").WithTags("My orders");
        customer.MapGet("",([AsParameters] MyOrdersFilter filter,HttpContext c,IMyOrders s,CancellationToken ct)=>s.MyOrders(Customer(c),filter,ct));
        customer.MapGet("/{id:guid}",(Guid id,HttpContext c,IMyOrders s,CancellationToken ct)=>s.MyOrder(Customer(c),id,ct));
    }
}
