using System.Security.Claims;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;

namespace GroupBuy.API;

public static class OperationsEndpoints
{
    private static Actor Actor(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!), c.User.IsInRole("Admin"), c.TraceIdentifier);
    private static PageRequest Page(int? page, int? pageSize) => new(page ?? 1, pageSize ?? 20);
    public static void MapOperations(this WebApplication app)
    {
        var seller = app.MapGroup("/api/v1/seller").RequireAuthorization("Seller").WithTags("Seller operations");
        seller.MapGet("/dashboard/summary", (HttpContext c, IOperations s, CancellationToken ct) => s.SellerSummary(Actor(c), ct));
        seller.MapGet("/campaigns", (int? page, int? pageSize, CampaignStatus? status, HttpContext c, IOperations s, CancellationToken ct) => s.SellerCampaigns(Actor(c), Page(page, pageSize), status, ct));
        seller.MapGet("/campaigns/{id:guid}/performance", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.CampaignPerformance(Actor(c), id, ct));
        seller.MapGet("/campaigns/{id:guid}/reservations", (Guid id, int? page, int? pageSize, ReservationStatus? status, HttpContext c, IOperations s, CancellationToken ct) => s.SellerReservations(Actor(c), id, Page(page, pageSize), status, ct));
        seller.MapGet("/inventory", (int? page, int? pageSize, HttpContext c, IOperations s, CancellationToken ct) => s.SellerInventory(Actor(c), Page(page, pageSize), ct));
        seller.MapPut("/inventory/{id:guid}", (Guid id, InventoryRequest r, HttpContext c, IOperations s, CancellationToken ct) => s.UpdateInventory(Actor(c), id, r, ct));
        seller.MapPost("/campaigns/{id:guid}/confirm", (Guid id, DecisionRequest r, HttpContext c, IOperations s, CancellationToken ct) => s.Decide(Actor(c), id, r, true, ct));
        seller.MapPost("/campaigns/{id:guid}/reject", (Guid id, DecisionRequest r, HttpContext c, IOperations s, CancellationToken ct) => s.Decide(Actor(c), id, r, false, ct));
        seller.MapGet("/pickups", (int? page, int? pageSize, PickupStatus? status, HttpContext c, IOperations s, CancellationToken ct) => s.Pickups(Actor(c), true, Page(page, pageSize), status, ct));
        seller.MapGet("/pickups/{id:guid}", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.SellerPickup(Actor(c), id, ct));
        seller.MapPost("/pickups/{id:guid}/ready", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.ReadyPickup(Actor(c), id, ct));
        seller.MapPost("/pickups/{id:guid}/collect", (Guid id, PickupCodeRequest r, HttpContext c, IOperations s, CancellationToken ct) => s.CollectPickup(Actor(c), id, r, ct)).RequireRateLimiting("pickup");
        var customer = app.MapGroup("/api/v1/me").RequireAuthorization("Customer").WithTags("Customer operations");
        customer.MapGet("/reservations/{id:guid}", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.ReservationDetail(Actor(c), id, ct));
        customer.MapGet("/pickups", (int? page, int? pageSize, PickupStatus? status, HttpContext c, IOperations s, CancellationToken ct) => s.Pickups(Actor(c), false, Page(page, pageSize), status, ct));
        customer.MapGet("/pickups/{id:guid}", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.CustomerPickup(Actor(c), id, ct));
        customer.MapPost("/pickups/{id:guid}/code", (Guid id, HttpContext c, IOperations s, CancellationToken ct) => s.IssuePickupCode(Actor(c), id, ct)).RequireRateLimiting("pickup");
        var notifications = app.MapGroup("/api/v1/me/notifications").RequireAuthorization().WithTags("Notifications");
        notifications.MapGet("/", (int? page, int? pageSize, bool? unreadOnly, HttpContext c, IOperations s, CancellationToken ct) => s.Notifications(Actor(c), Page(page, pageSize), unreadOnly ?? false, ct));
        notifications.MapPost("/{id:guid}/read", async (Guid id, HttpContext c, IOperations s, CancellationToken ct) => { await s.ReadNotification(Actor(c), id, ct); return Results.NoContent(); });
        app.MapGet("/api/v1/admin/operations/health", (HttpContext c, IOperations s, CancellationToken ct) => s.OperationsHealth(Actor(c), ct)).RequireAuthorization("Admin").RequireRateLimiting("admin");
    }
}

