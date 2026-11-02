using System.Security.Claims;
using GroupBuy.Application;
using GroupBuy.Domain;

namespace GroupBuy.API;
public static class AdministrationEndpoints
{
    private static Actor Actor(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!), c.User.IsInRole("Admin"), c.TraceIdentifier);
    private static PageRequest Page(int? page, int? pageSize) => new(page ?? 1, pageSize ?? 20);
    public static void MapAdministration(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization("Admin").RequireRateLimiting("admin").WithTags("Marketplace administration");
        admin.MapGet("/dashboard/summary", (HttpContext c, IAdministration s, CancellationToken ct) => s.AdminSummary(Actor(c), ct));
        admin.MapGet("/sellers", (int? page, int? pageSize, VerificationStatus? status, string? search, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminSellers(Actor(c), Page(page, pageSize), status, search, ct));
        admin.MapGet("/sellers/{id:guid}", (Guid id, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminSeller(Actor(c), id, ct));
        admin.MapGet("/campaigns", (int? page, int? pageSize, CampaignStatus? status, string? search, bool? failedOnly, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminCampaigns(Actor(c), Page(page, pageSize), status, search, failedOnly ?? false, ct));
        admin.MapGet("/campaigns/{id:guid}", (Guid id, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminCampaign(Actor(c), id, ct));
        admin.MapGet("/categories", (int? page, int? pageSize, bool? active, string? search, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminCategories(Actor(c), Page(page, pageSize), active, search, ct));
        admin.MapGet("/products", (int? page, int? pageSize, ResourceStatus? status, string? search, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminProducts(Actor(c), Page(page, pageSize), status, search, ct));
        admin.MapGet("/reservations", (int? page, int? pageSize, ReservationStatus? status, Guid? campaignId, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminReservations(Actor(c), Page(page, pageSize), status, campaignId, ct));
        admin.MapGet("/pickups", (int? page, int? pageSize, PickupStatus? status, Guid? campaignId, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminPickups(Actor(c), Page(page, pageSize), status, campaignId, ct));
        admin.MapGet("/audit-logs", (int? page, int? pageSize, string? action, string? entityType, Guid? actorId, Guid? entityId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, HttpContext c, IAdministration s, CancellationToken ct) => s.AdminAudit(Actor(c), Page(page, pageSize), action, entityType, actorId, entityId, fromUtc, toUtc, ct));
    }
}
