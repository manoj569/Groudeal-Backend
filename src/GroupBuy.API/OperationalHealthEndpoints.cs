using GroupBuy.Application;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GroupBuy.API;
public static class OperationalHealthEndpoints
{
    public static void MapOperationalHealth(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).ExcludeFromDescription();
        app.MapGet("/health/ready", async (MarketplaceDb db, WorkerHealthState health, TimeProvider clock, HttpContext context) => {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try {
                var worker = health.Read();
                var activity = worker.Running ? worker.LastProgressAtUtc : worker.LastCompletedAtUtc;
                var healthy = worker.LastCompletedAtUtc != null && activity > clock.GetUtcNow().AddSeconds(-45) && worker.LastBatchErrorType is null;
                if (!healthy || !await db.Database.CanConnectAsync(timeout.Token) || (await db.Database.GetPendingMigrationsAsync(timeout.Token)).Any())
                    return Results.Json(new { status = "not_ready" }, statusCode: 503);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception) { return Results.Json(new { status = "not_ready" }, statusCode: 503); }
        }).ExcludeFromDescription();
    }
}
