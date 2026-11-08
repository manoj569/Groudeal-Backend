using System.Security.Claims;
using GroupBuy.Application;
using GroupBuy.Domain;

namespace GroupBuy.API;

public static class DiscoveryEndpoints
{
    public static void MapDiscovery(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/discovery").WithTags("Discovery").ProducesProblem(400).ProducesProblem(429).ProducesProblem(503);
        api.MapGet("/service-area", (string? postalCode, string? serviceArea, IDiscovery s) => s.ServiceArea(new(postalCode, serviceArea)));
        api.MapGet("/home", (string? postalCode, string? serviceArea, Guid? categoryId, IDiscovery s, CancellationToken ct) => s.Home(new(postalCode, serviceArea, categoryId), ct));
        api.MapGet("/search", (string q, string? postalCode, string? serviceArea, Guid? categoryId, int? page, int? pageSize, IDiscovery s, CancellationToken ct)
            => s.Search(q, new(postalCode, serviceArea, categoryId), new(page ?? 1, pageSize ?? 20), ct));
        api.MapGet("/search/suggestions", (string q, string? postalCode, string? serviceArea, Guid? categoryId, int? limit, IDiscovery s, CancellationToken ct)
            => s.Suggestions(q, new(postalCode, serviceArea, categoryId), limit ?? 8, ct));
        foreach (var ranking in new[] { "trending", "near-you", "new" })
        {
            var sort = ranking;
            api.MapGet("/deals/" + sort, (string? postalCode, string? serviceArea, Guid? categoryId, int? page, int? pageSize, IDiscovery s, CancellationToken ct)
                => s.Deals(sort, new(postalCode, serviceArea, categoryId), new(page ?? 1, pageSize ?? 20), ct));
        }
        api.MapGet("/demands/popular", (string? postalCode, string? serviceArea, Guid? categoryId, int? page, int? pageSize, IDiscovery s, CancellationToken ct)
            => s.Demands(new(postalCode, serviceArea, categoryId), new(page ?? 1, pageSize ?? 20), ct));
        app.MapGet("/api/v1/categories/{slug}/discovery", (string slug, string? postalCode, string? serviceArea, int? page, int? pageSize, IDiscovery s, CancellationToken ct)
            => s.Category(slug, new(postalCode, serviceArea), new(page ?? 1, pageSize ?? 20), ct)).WithTags("Discovery");
        var customer = api.MapGroup("/demands").RequireAuthorization("Customer").RequireRateLimiting("customer-auth");
        static Actor Actor(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!), c.User.IsInRole("Admin"), c.TraceIdentifier);
        customer.MapPost("/similar", (CreateDemandRequest request, HttpContext c, IDiscovery s, CancellationToken ct) => s.PreviewDemand(Actor(c), request, ct));
        customer.MapGet("/mine", (DemandStatus? status, DemandType? type, Guid? categoryId, string? serviceArea, int? page, int? pageSize, HttpContext c, IDiscovery s, CancellationToken ct) => s.MyDemands(Actor(c), new(status, type, categoryId, serviceArea), new(page ?? 1, pageSize ?? 20), ct));
        api.MapGet("/demands/{id:guid}", (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => s.DemandDetail(id, c.User.Identity?.IsAuthenticated == true && c.User.IsInRole("Customer") ? Actor(c) : null, ct));
        var admin = app.MapGroup("/api/v1/admin/discovery/demands").RequireAuthorization("Admin").RequireRateLimiting("admin").WithTags("Discovery administration");
        admin.MapGet("/", (DemandStatus? status, DemandType? type, Guid? categoryId, string? serviceArea, int? page, int? pageSize, HttpContext c, IDiscovery s, CancellationToken ct) => s.AdminDemands(Actor(c), new(status, type, categoryId, serviceArea), new(page ?? 1, pageSize ?? 20), ct));
        admin.MapGet("/{id:guid}", (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => s.AdminDemand(Actor(c), id, ct));
        admin.MapGet("/{id:guid}/audit", (Guid id, int? page, int? pageSize, HttpContext c, IDiscovery s, CancellationToken ct) => s.DemandAuditHistory(Actor(c), id, new(page ?? 1, pageSize ?? 20), ct));
        admin.MapPost("/{id:guid}/moderate", async (Guid id, ModerateDemandRequest request, HttpContext c, IDiscovery s, CancellationToken ct) => { await s.ModerateDemand(Actor(c), id, request, ct); return Results.NoContent(); });
        customer.MapPost("/", (CreateDemandRequest request, HttpContext c, IDiscovery s, CancellationToken ct) => s.CreateDemand(Actor(c), request, ct));
        customer.MapPost("/{id:guid}/join", (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => s.JoinDemand(Actor(c), id, false, ct));
        customer.MapPost("/{id:guid}/withdraw", (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => s.JoinDemand(Actor(c), id, true, ct));
        customer.MapGet("/{id:guid}/membership", (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => s.Membership(Actor(c), id, ct));
        app.MapPost("/api/v1/admin/discovery/demands/{id:guid}/close", async (Guid id, HttpContext c, IDiscovery s, CancellationToken ct) => { await s.CloseDemand(Actor(c), id, ct); return Results.NoContent(); })
            .RequireAuthorization("Admin").RequireRateLimiting("admin").WithTags("Discovery administration");
    }
}
