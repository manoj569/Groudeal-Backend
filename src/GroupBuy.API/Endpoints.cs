using System.Security.Claims;
using GroupBuy.Application;
namespace GroupBuy.API;
public static class Endpoints
{
    private static Actor Actor(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!), c.User.IsInRole("Admin"), c.TraceIdentifier);
    private static PageRequest Page(int? page, int? pageSize) => new(page ?? 1, pageSize ?? 20);
    public static void MapMarketplace(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        var auth = api.MapGroup("/auth").WithTags("Authentication");
        auth.MapPost("/register", async (RegisterRequest r, IIdentityService s, CancellationToken ct) => { var user = await s.Register(r, ct); return Results.Created("/api/v1/auth/me", user); }).Produces<UserResponse>(201).RequireRateLimiting("auth");
        auth.MapPost("/login", (LoginRequest r, IIdentityService s, CancellationToken ct) => s.Login(r, ct)).RequireRateLimiting("auth");
        auth.MapGet("/me", (HttpContext c, IIdentityService s, CancellationToken ct) => s.Me(Actor(c).UserId, ct)).RequireAuthorization();
        auth.MapPost("/logout", async (HttpContext c, IIdentityService s, CancellationToken ct) => { await s.Logout(Actor(c).UserId, ct); return Results.NoContent(); }).RequireAuthorization();
        api.MapGet("/categories", (int? page, int? pageSize, IMarketplace s, CancellationToken ct) => s.Categories(Page(page, pageSize), ct)).WithTags("Categories");
        api.MapGet("/categories/{slug}", (string slug, IMarketplace s, CancellationToken ct) => s.Category(slug, ct)).WithTags("Categories");
        api.MapGet("/sellers", (int? page, int? pageSize, IMarketplace s, CancellationToken ct) => s.Sellers(Page(page, pageSize), ct)).WithTags("Sellers");
        api.MapGet("/sellers/{id:guid}", (Guid id, IMarketplace s, CancellationToken ct) => s.Seller(id, ct)).WithTags("Sellers");
        var seller = api.MapGroup("/seller").RequireAuthorization().WithTags("Seller management");
        seller.MapGet("/profile", (HttpContext c, IMarketplace s, CancellationToken ct) => s.SellerProfile(Actor(c), ct));
        seller.MapPost("/profile", async (SellerRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => { var result = await s.SaveSeller(Actor(c), r, true, ct); return Results.Created($"/api/v1/sellers/{result.Id}", result); }).Produces<SellerResponse>(201);
        seller.MapPut("/profile", (SellerRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.SaveSeller(Actor(c), r, false, ct));
        seller.MapGet("/dashboard", (HttpContext c, IMarketplace s, CancellationToken ct) => s.Dashboard(Actor(c), ct));
        api.MapGet("/products", (int? page, int? pageSize, IMarketplace s, CancellationToken ct) => s.Products(Page(page, pageSize), ct)).WithTags("Products");
        api.MapGet("/products/{id:guid}", (Guid id, IMarketplace s, CancellationToken ct) => s.Product(id, ct)).WithTags("Products");
        seller.MapPost("/products", async (ProductRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => { var result = await s.SaveProduct(Actor(c), null, r, ct); return Results.Created($"/api/v1/products/{result.Id}", result); }).RequireAuthorization("Seller").Produces<ProductResponse>(201);
        seller.MapGet("/products", (int? page, int? pageSize, GroupBuy.Domain.ResourceStatus? status, HttpContext c, IMarketplace s, CancellationToken ct) => s.SellerProducts(Actor(c), Page(page, pageSize), status, ct)).RequireAuthorization("Seller");
        seller.MapPut("/products/{id:guid}/status", (Guid id, ProductStatusRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.SetProductStatus(Actor(c), id, r, ct)).RequireAuthorization("Seller");
        seller.MapPut("/products/{id:guid}", (Guid id, ProductRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.SaveProduct(Actor(c), id, r, ct)).RequireAuthorization("Seller");
        api.MapGet("/group-buys", (int? page, int? pageSize, IMarketplace s, CancellationToken ct) => s.Campaigns(Page(page, pageSize), ct)).WithTags("Group buys");
        api.MapGet("/group-buys/{id:guid}", (Guid id, IMarketplace s, CancellationToken ct) => s.Campaign(id, ct)).WithTags("Group buys");
        seller.MapPost("/group-buys", async (CampaignRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => { var result = await s.SaveCampaign(Actor(c), null, r, ct); return Results.Created($"/api/v1/group-buys/{result.Id}", result); }).RequireAuthorization("Seller").Produces<CampaignResponse>(201);
        seller.MapPut("/group-buys/{id:guid}", (Guid id, CampaignRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.SaveCampaign(Actor(c), id, r, ct)).RequireAuthorization("Seller");
        seller.MapPost("/group-buys/{id:guid}/submit", (Guid id, HttpContext c, IMarketplace s, CancellationToken ct) => s.Submit(Actor(c), id, ct)).RequireAuthorization("Seller");
        seller.MapPost("/group-buys/{id:guid}/transition", (Guid id, TransitionRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.Transition(Actor(c), id, r, ct)).RequireAuthorization("Seller");
        api.MapPost("/group-buys/{id:guid}/reservations", async (Guid id, ReservationRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => { var result = await s.Reserve(Actor(c), id, r, ct); return Results.Created($"/api/v1/me/reservations/{result.Id}", result); }).RequireAuthorization("Customer").Produces<ReservationResponse>(201).WithTags("Reservations");
        api.MapGet("/me/reservations", (int? page, int? pageSize, HttpContext c, IMarketplace s, CancellationToken ct) => s.Reservations(Actor(c), Page(page, pageSize), ct)).RequireAuthorization("Customer").WithTags("Reservations");
        api.MapPost("/me/reservations/{id:guid}/cancel", (Guid id, HttpContext c, IMarketplace s, CancellationToken ct) => s.Cancel(Actor(c), id, ct)).RequireAuthorization("Customer").WithTags("Reservations");
        var admin = api.MapGroup("/admin").RequireAuthorization("Admin").RequireRateLimiting("admin").WithTags("Administration");
        admin.MapGet("/sellers/pending", (int? page, int? pageSize, HttpContext c, IMarketplace s, CancellationToken ct) => s.PendingSellers(Actor(c), Page(page, pageSize), ct));
        admin.MapPost("/sellers/{id:guid}/approve", (Guid id, ReviewRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.ReviewSeller(Actor(c), id, true, r, ct));
        admin.MapPost("/sellers/{id:guid}/reject", (Guid id, ReviewRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.ReviewSeller(Actor(c), id, false, r, ct));
        admin.MapGet("/group-buys/pending", (int? page, int? pageSize, HttpContext c, IMarketplace s, CancellationToken ct) => s.PendingCampaigns(Actor(c), Page(page, pageSize), ct));
        admin.MapPost("/group-buys/{id:guid}/approve", (Guid id, ReviewRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.ReviewCampaign(Actor(c), id, true, r, ct));
        admin.MapPost("/group-buys/{id:guid}/reject", (Guid id, ReviewRequest r, HttpContext c, IMarketplace s, CancellationToken ct) => s.ReviewCampaign(Actor(c), id, false, r, ct));

        api.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
    }
}


