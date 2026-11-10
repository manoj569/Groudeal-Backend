using System.Security.Claims;
using GroupBuy.Application;
namespace GroupBuy.API;
public static class GroupDealEndpoints
{
    private static Actor? Caller(HttpContext c) => c.User.Identity?.IsAuthenticated == true
        ? new(Guid.Parse(c.User.FindFirstValue("sub")!), c.User.IsInRole("Admin"), c.TraceIdentifier) : null;
    public static void MapGroupDeals(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapPost("/admin/group-buys/{id:guid}/cancel", (Guid id, ReviewRequest r, HttpContext c, IGroupDeals s, CancellationToken ct) => s.CancelDeal(Caller(c)!, id, r, ct)).RequireAuthorization("Admin").RequireRateLimiting("admin");
        api.MapGet("/group-buys/deals", ([AsParameters] GroupDealFilter filter, HttpContext c, IGroupDeals s, CancellationToken ct) => s.Deals(filter, Caller(c), ct));
        api.MapGet("/group-buys/{id:guid}/deal", (Guid id, HttpContext c, IGroupDeals s, CancellationToken ct) => s.Deal(id, Caller(c), ct));
        api.MapGet("/seller/group-buys/{id:guid}/offer", (Guid id, HttpContext c, IGroupDeals s, CancellationToken ct) => s.Offer(Caller(c)!, id, false, ct)).RequireAuthorization("Seller");
        api.MapGet("/admin/group-buys/{id:guid}/offer", (Guid id, HttpContext c, IGroupDeals s, CancellationToken ct) => s.Offer(Caller(c)!, id, true, ct)).RequireAuthorization("Admin").RequireRateLimiting("admin");
        api.MapGet("/seller/demand-opportunities", (int? page, int? pageSize, string serviceArea, HttpContext c, IGroupDeals s, CancellationToken ct) => s.Opportunities(Caller(c)!, new(page ?? 1, pageSize ?? 20), serviceArea, ct)).RequireAuthorization("Seller");
        api.MapPut("/seller/group-buys/{id:guid}/demand-offer", (Guid id, LinkDemandOfferRequest r, HttpContext c, IGroupDeals s, CancellationToken ct) => s.LinkDemandOffer(Caller(c)!, id, r, ct)).RequireAuthorization("Seller");
    }
}
