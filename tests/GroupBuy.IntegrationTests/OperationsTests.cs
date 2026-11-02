using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    private static async Task Tick(ApiFactory f)
    {
        await using var scope = f.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ICampaignExpiryProcessor>().Process(default);
    }
    private static async Task<GroupBuyCampaign> StoredCampaign(ApiFactory f, Guid id)
    {
        await using var scope = f.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Campaigns.AsNoTracking().SingleAsync(c => c.Id == id);
    }
    private static async Task SetDeadline(ApiFactory f, Guid id, bool confirmation)
    {
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        if (confirmation) await db.Campaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ConfirmationDeadlineUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        else
        {
            await db.Campaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.PickupDeadlineUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            await db.Pickups.Where(p => p.CampaignId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeadlineUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
    }
    private static async Task<PickupDetailResponse> ConfirmAndReady(HttpClient seller, HttpClient customer, Guid campaign)
    {
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{campaign}/reservations", new ReservationRequest(2, "pickup-reservation")));
        await Read<OperationCampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{campaign}/confirm", new DecisionRequest("confirm-pickup-test", null)));
        var pickup = (await Read<PageResponse<PickupDetailResponse>>(await customer.GetAsync("/api/v1/me/pickups"))).Items.Single();
        return await Read<PickupDetailResponse>(await seller.PostAsync($"/api/v1/seller/pickups/{pickup.Id}/ready", null));
    }

    [Fact]
    public async Task Seller_dashboard_inventory_and_reservations_are_owner_scoped()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var reservation = await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "dashboard-quantity")));
        var summary = await Read<SellerSummaryResponse>(await seller.GetAsync("/api/v1/seller/dashboard/summary"));
        Assert.Equal(1, summary.Products); Assert.Equal(1, summary.ActiveCampaigns); Assert.Equal(1, summary.ActiveReservations); Assert.Equal(80, summary.ReservationValueInr); Assert.Equal(0, summary.FulfilledValueInr);
        Assert.Equal(1, (await Read<DashboardResponse>(await seller.GetAsync("/api/v1/seller/dashboard"))).ActiveReservations);
        var campaigns = await Read<PageResponse<OperationCampaignResponse>>(await seller.GetAsync("/api/v1/seller/campaigns?status=Open&pageSize=1")); Assert.Single(campaigns.Items); Assert.Equal(10, campaigns.Items[0].CommittedQuantity);
        var reservations = await Read<PageResponse<SellerReservationResponse>>(await seller.GetAsync($"/api/v1/seller/campaigns/{x.Campaign}/reservations?status=Active")); Assert.Equal(reservation.Id, Assert.Single(reservations.Items).Id);
        var performance = await Read<CampaignPerformanceResponse>(await seller.GetAsync($"/api/v1/seller/campaigns/{x.Campaign}/performance")); Assert.Equal(1, performance.TotalReservations); Assert.Equal(0, performance.FulfilledValueInr);
        using var other = f.CreateClient(); var user = await Register(other, "isolated-seller"); await Login(other, user);
        var profile = await Read<SellerResponse>(await other.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Isolated")));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(other, user);
        Assert.Empty((await Read<PageResponse<OperationCampaignResponse>>(await other.GetAsync("/api/v1/seller/campaigns"))).Items);
        Assert.Equal(0, (await Read<SellerSummaryResponse>(await other.GetAsync("/api/v1/seller/dashboard/summary"))).ActiveReservations);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/seller/campaigns/{x.Campaign}/reservations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/seller/campaigns/{x.Campaign}/performance")).StatusCode);
        var c = await StoredCampaign(f, x.Campaign);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/seller/inventory/{c.ProductVariantId}", new InventoryRequest(100, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/me/reservations/{reservation.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/seller/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Inventory_versions_and_concurrent_campaign_submissions_prevent_double_allocation()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var c = await StoredCampaign(f, x.Campaign);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{c.ProductVariantId}", new InventoryRequest(9, 1))).StatusCode);
        var updates = await Task.WhenAll(seller.PutAsJsonAsync($"/api/v1/seller/inventory/{c.ProductVariantId}", new InventoryRequest(20, 1)), seller.PutAsJsonAsync($"/api/v1/seller/inventory/{c.ProductVariantId}", new InventoryRequest(20, 1)));
        Assert.Single(updates, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(updates, r => r.StatusCode == HttpStatusCode.Conflict);
        var request = new CampaignRequest(c.ProductVariantId, c.SellerLocationId, "Competing stock", "", 100, 80, 10, 1, 10, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var drafts = new[] { await Read<CampaignResponse>(await seller.PostAsJsonAsync("/api/v1/seller/group-buys", request)), await Read<CampaignResponse>(await seller.PostAsJsonAsync("/api/v1/seller/group-buys", request)) };
        var submissions = await Task.WhenAll(drafts.Select(d => seller.PostAsync($"/api/v1/seller/group-buys/{d.Id}/submit", null)));
        Assert.Single(submissions, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(submissions, r => r.StatusCode == HttpStatusCode.Conflict);
        var inventory = Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items);
        Assert.Equal(20, inventory.CommittedQuantity); Assert.Equal(20, inventory.AvailableQuantity); Assert.Equal(2, inventory.Version);
        var pending = (await Read<PageResponse<OperationCampaignResponse>>(await seller.GetAsync("/api/v1/seller/campaigns?status=PendingApproval"))).Items.Single();
        await Read<CampaignResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{pending.Campaign.Id}/reject", new ReviewRequest("Inventory test")));
        Assert.Equal(10, Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items).CommittedQuantity);
    }

    [Fact]
    public async Task Confirmation_is_transactional_idempotent_and_not_an_order_payment()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var r = await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "confirmation-stock")));
        var before = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.AwaitingSellerConfirmation, before.Status); Assert.NotNull(before.ConfirmationDeadlineUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/v1/me/reservations/{r.Id}/cancel", null)).StatusCode);
        var request = new DecisionRequest("concurrent-confirm", "Inventory checked");
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/confirm", request)));
        foreach (var response in responses) Assert.Equal(CampaignStatus.Confirmed, (await Read<OperationCampaignResponse>(response)).Campaign.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/confirm", request with { Reason = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/reject", new DecisionRequest("conflicting-decision", "Unavailable"))).StatusCode);
        var detail = await Read<ReservationDetailResponse>(await customer.GetAsync($"/api/v1/me/reservations/{r.Id}")); Assert.Equal("NotTracked", detail.PaymentStatus); Assert.Equal("ViewPickup", detail.NextAction);
        var pickup = Assert.Single((await Read<PageResponse<PickupDetailResponse>>(await customer.GetAsync("/api/v1/me/pickups"))).Items); Assert.Equal(PickupStatus.Pending, pickup.Status); Assert.Equal(PickupPaymentPolicy.NotTrackedOfflineResponsibility, pickup.PaymentPolicy);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null)).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.Pickups.CountAsync(p => p.CampaignId == x.Campaign)); Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "InventoryConfirmed"));
        Assert.NotNull((await db.Campaigns.SingleAsync(c => c.Id == x.Campaign)).DecisionActorId);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.EntityId == x.Campaign && n.Type == NotificationType.CampaignConfirmed));
    }

    [Fact]
    public async Task Seller_rejection_releases_inventory_and_preserves_terminal_reservation_snapshots()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "reject-reservation")));
        var request = new DecisionRequest("reject-stock-test", "Stock spoiled");
        for (var i = 0; i < 2; i++) Assert.Equal(CampaignStatus.Failed, (await Read<OperationCampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/reject", request))).Campaign.Status);
        var c = await StoredCampaign(f, x.Campaign); Assert.Equal(0, c.CommittedQuantity); Assert.Equal("Stock spoiled", c.DecisionReason); Assert.False(c.DecisionApproved);
        var inventory = Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items); Assert.Equal(2, inventory.AvailableQuantity); Assert.Equal(0, inventory.CommittedQuantity);
        var r = Assert.Single((await Read<PageResponse<ReservationResponse>>(await customer.GetAsync("/api/v1/me/reservations"))).Items); Assert.Equal(ReservationStatus.Expired, r.Status); Assert.Equal(160, r.TotalPriceSnapshot);
        var notifications = await Read<PageResponse<NotificationResponse>>(await customer.GetAsync("/api/v1/me/notifications")); Assert.Single(notifications.Items, n => n.Type == NotificationType.CampaignRejected);
    }

    [Fact]
    public async Task Confirmation_expiry_survives_new_processor_scopes_and_cannot_be_confirmed_late()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "late-confirmation")));
        await SetDeadline(f, x.Campaign, true);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/confirm", new DecisionRequest("too-late-confirmation", null))).StatusCode);
        await Tick(f); await Tick(f);
        var c = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.Failed, c.Status); Assert.Equal(0, c.CommittedQuantity);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "ConfirmationExpired"));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.EntityId == x.Campaign && n.Type == NotificationType.CampaignExpired));
    }

    [Fact]
    public async Task Pickup_code_is_hashed_single_use_and_concurrent_collection_completes_once()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var pickup = await ConfirmAndReady(seller, customer, x.Campaign);
        var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null));
        Assert.Equal(32, code.Code.Length);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var p = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Pickups.AsNoTracking().SingleAsync(p => p.Id == pickup.Id);
            Assert.NotNull(p.CodeHash); Assert.NotEqual(code.Code, p.CodeHash); Assert.Equal(64, p.CodeHash.Length);
        }
        Assert.DoesNotContain(code.Code, await customer.GetStringAsync($"/api/v1/me/pickups/{pickup.Id}"));
        Assert.DoesNotContain("codeHash", await seller.GetStringAsync("/api/v1/seller/pickups"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await customer.PostAsync($"/api/v1/me/pickups/{pickup.Id}/code", null)).StatusCode);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => seller.PostAsJsonAsync($"/api/v1/seller/pickups/{pickup.Id}/collect", new PickupCodeRequest(code.Code))));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{pickup.Id}/collect", new PickupCodeRequest(code.Code))).StatusCode);
        var c = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.Completed, c.Status); Assert.Equal(2, c.FulfilledQuantity); Assert.Equal(0, c.CommittedQuantity);
        var inventory = Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items); Assert.Equal(0, inventory.AvailableQuantity); Assert.Equal(2, inventory.FulfilledQuantity); Assert.Equal(0, inventory.ReservedQuantity);
        var metrics = await Read<SellerSummaryResponse>(await seller.GetAsync("/api/v1/seller/dashboard/summary")); Assert.Equal(1, metrics.CompletedCampaigns); Assert.Equal(160, metrics.FulfilledValueInr);
        await using var verify = f.Services.CreateAsyncScope(); var db = verify.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Null((await db.Pickups.SingleAsync(p => p.Id == pickup.Id)).CodeHash); Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == pickup.Id && a.Action == "PickupCollected"));
    }

    [Fact]
    public async Task Invalid_pickup_attempts_are_persisted_and_lock_even_the_correct_code()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await ConfirmAndReady(seller, customer, x.Campaign); var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{p.Id}/code", null));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(new string('0', 32)))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(code.Code))).StatusCode);
        await using var scope = f.Services.CreateAsyncScope(); var stored = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Pickups.SingleAsync(v => v.Id == p.Id);
        Assert.Equal(5, stored.FailedAttempts); Assert.NotNull(stored.LockedUntilUtc); Assert.Equal(PickupStatus.ReadyForPickup, stored.Status);
    }

    [Fact]
    public async Task Pickup_codes_and_notifications_cannot_be_accessed_by_other_sellers_or_customers()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await ConfirmAndReady(seller, customer, x.Campaign);
        using var other = f.CreateClient(); var user = await Register(other, "pickup-isolation"); await Login(other, user);
        var profile = await Read<SellerResponse>(await other.PostAsJsonAsync("/api/v1/seller/profile", SellerRequest("Other pickup shop")));
        await Read<SellerReviewResponse>(await admin.PostAsJsonAsync($"/api/v1/admin/sellers/{profile.Id}/approve", new ReviewRequest(null))); await Login(other, user);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/me/pickups/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/me/pickups/{p.Id}/code", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/seller/pickups/{p.Id}/ready", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(new string('0', 32)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(new string('0', 32)))).StatusCode);
        var notifications = await Read<PageResponse<NotificationResponse>>(await customer.GetAsync("/api/v1/me/notifications"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/me/notifications/{notifications.Items[0].Id}/read", null)).StatusCode);
        Assert.Empty((await Read<PageResponse<PickupDetailResponse>>(await other.GetAsync("/api/v1/me/pickups"))).Items);
    }

    [Fact]
    public async Task Pickup_expiry_releases_stock_and_never_completes_an_uncollected_campaign()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await ConfirmAndReady(seller, customer, x.Campaign); var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{p.Id}/code", null));
        await SetDeadline(f, x.Campaign, false);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(code.Code))).StatusCode);
        await Tick(f); await Tick(f);
        Assert.Equal(PickupStatus.NotCollected, (await Read<PickupDetailResponse>(await customer.GetAsync($"/api/v1/me/pickups/{p.Id}"))).Status);
        var c = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.Failed, c.Status); Assert.Equal(0, c.CommittedQuantity); Assert.Equal(0, c.FulfilledQuantity);
        var inventory = Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items); Assert.Equal(2, inventory.AvailableQuantity);
    }

    [Fact]
    public async Task Notification_delivery_is_transactional_readable_and_idempotent()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await ConfirmAndReady(seller, customer, x.Campaign);
        await Read<PickupDetailResponse>(await seller.PostAsync($"/api/v1/seller/pickups/{p.Id}/ready", null));
        var notices = await Read<PageResponse<NotificationResponse>>(await customer.GetAsync("/api/v1/me/notifications?unreadOnly=true"));
        Assert.Single(notices.Items, n => n.Type == NotificationType.PickupReady); Assert.Single(notices.Items, n => n.Type == NotificationType.CampaignConfirmed); Assert.Single(notices.Items, n => n.Type == NotificationType.ReservationCreated);
        var sellerNotices = await Read<PageResponse<NotificationResponse>>(await seller.GetAsync("/api/v1/me/notifications")); Assert.Single(sellerNotices.Items, n => n.Type == NotificationType.SellerConfirmationRequired);
        var id = notices.Items[0].Id;
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/v1/me/notifications/{id}/read", null)).StatusCode);
        Assert.DoesNotContain((await Read<PageResponse<NotificationResponse>>(await customer.GetAsync("/api/v1/me/notifications?unreadOnly=true"))).Items, n => n.Id == id);
        // Roll back a notification together with a failed business transaction.
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var count = await db.Notifications.CountAsync();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Notifications.Add(new InternalNotification { RecipientUserId = x.CustomerId, EntityId = p.Id, Type = NotificationType.PickupCompleted, DeduplicationKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(); await tx.RollbackAsync();
        }
        Assert.Equal(count, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Worker_isolates_poison_campaign_persists_backoff_and_recovers_in_new_scope()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(2, "worker-retry-key")));
        await SetDeadline(f, x.Campaign, true);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>(); var source = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == x.Campaign);
            db.Campaigns.Add(new GroupBuyCampaign { SellerId = source.SellerId, ProductVariantId = source.ProductVariantId, SellerLocationId = source.SellerLocationId, Title = "Independent expiry", ReferenceUnitPrice = 100, GroupUnitPrice = 80, TargetQuantity = 1, MinimumReservationQuantity = 1, MaximumReservationQuantity = 1, Status = CampaignStatus.Open, StartsAtUtc = DateTimeOffset.UtcNow.AddHours(-2), ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION audit_test_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Action\" = 'ConfirmationExpired' THEN RAISE EXCEPTION 'injected failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER audit_test_failure BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION audit_test_failure();");
        }
        try
        {
            await Tick(f);
            var failed = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.AwaitingSellerConfirmation, failed.Status); Assert.True(failed.WorkerFailureCount >= 1); Assert.True(failed.WorkerRetryAtUtc > DateTimeOffset.UtcNow);
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.True(await db.Campaigns.AnyAsync(c => c.SellerId == failed.SellerId && c.Title == "Independent expiry" && c.Status == CampaignStatus.Expired));
            Assert.Equal(0, await db.Notifications.CountAsync(n => n.EntityId == x.Campaign && n.Type == NotificationType.CampaignExpired));
            var health = await Read<OperationsHealthResponse>(await admin.GetAsync("/api/v1/admin/operations/health")); Assert.True(health.RetryingCampaigns >= 1); Assert.NotNull(health.Worker); Assert.NotNull(health.Worker.LastStartedAtUtc);
        }
        finally
        {
            await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER audit_test_failure ON audit_logs; DROP FUNCTION audit_test_failure();");
            await db.Campaigns.Where(c => c.Id == x.Campaign).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkerRetryAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        using var restarted = new ApiFactory(fixture.Connection); using var restartedClient = restarted.CreateClient();
        await Tick(restarted); await Tick(restarted);
        var recovered = await StoredCampaign(restarted, x.Campaign); Assert.Equal(CampaignStatus.Failed, recovered.Status); Assert.Equal(0, recovered.WorkerFailureCount); Assert.Null(recovered.WorkerRetryAtUtc);
        await using var verify = f.Services.CreateAsyncScope(); Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<MarketplaceDb>().AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "ConfirmationExpired"));
    }

    [Fact]
    public async Task Worker_cancellation_does_not_change_campaign_or_generate_notifications()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var scope = f.Services.CreateAsyncScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ICampaignExpiryProcessor>().Process(cancellation.Token));
        Assert.Equal(CampaignStatus.Open, (await StoredCampaign(f, x.Campaign)).Status);
    }

    [Fact]
    public async Task Cancellation_racing_final_reservation_preserves_active_quantity_and_confirmation_boundary()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var first = await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "cancel-race-first")));
        var operations = await Task.WhenAll(customer.PostAsync($"/api/v1/me/reservations/{first.Id}/cancel", null), customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, "cancel-race-second")));
        Assert.Equal(HttpStatusCode.Created, operations[1].StatusCode); Assert.Contains(operations[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        var c = await StoredCampaign(f, x.Campaign);
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var active = await db.Reservations.Where(r => r.CampaignId == x.Campaign && r.Status == ReservationStatus.Active).SumAsync(r => r.Quantity);
        Assert.Equal(active, c.ReservedQuantity); Assert.InRange(c.ReservedQuantity, 1, 2); Assert.Equal(2, c.CommittedQuantity);
        Assert.Equal(active == 2 ? CampaignStatus.AwaitingSellerConfirmation : CampaignStatus.Open, c.Status);
    }

    [Fact]
    public async Task Partial_collection_does_not_complete_campaign_and_uncollected_stock_is_released()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        for (var i = 0; i < 2; i++) await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, $"partial-pickup-{i:000}")));
        await Read<OperationCampaignResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/confirm", new DecisionRequest("partial-fulfillment", null)));
        var p = (await Read<PageResponse<PickupDetailResponse>>(await customer.GetAsync("/api/v1/me/pickups"))).Items[0];
        await Read<PickupDetailResponse>(await seller.PostAsync($"/api/v1/seller/pickups/{p.Id}/ready", null));
        var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{p.Id}/code", null));
        await Read<PickupDetailResponse>(await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(code.Code)));
        var partial = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.FulfillmentInProgress, partial.Status); Assert.Equal(1, partial.FulfilledQuantity); Assert.Equal(1, partial.CommittedQuantity);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync($"/api/v1/seller/group-buys/{x.Campaign}/transition", new TransitionRequest(CampaignStatus.Completed))).StatusCode);
        await SetDeadline(f, x.Campaign, false); await Tick(f);
        var terminal = await StoredCampaign(f, x.Campaign); Assert.Equal(CampaignStatus.Failed, terminal.Status); Assert.Equal(1, terminal.FulfilledQuantity); Assert.Equal(0, terminal.CommittedQuantity);
        var inventory = Assert.Single((await Read<PageResponse<InventoryResponse>>(await seller.GetAsync("/api/v1/seller/inventory"))).Items); Assert.Equal(1, inventory.AvailableQuantity); Assert.Equal(1, inventory.FulfilledQuantity);
    }

    [Fact]
    public async Task Inventory_unit_precision_and_missing_request_fields_are_enforced_over_http()
    {
        var x = await Setup(); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var piece = await Read<ProductResponse>(await seller.PostAsJsonAsync("/api/v1/seller/products", new ProductRequest(CategorySeed.All[1].Id, "Pack", "pack-unit", "", ProductType.Physical, Guid.NewGuid().ToString("N"), "Pack", UnitOfMeasure.Pack, 1, "{}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{piece.Variants[0].Id}", new InventoryRequest(1.5m, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PutAsJsonAsync($"/api/v1/seller/inventory/{piece.Variants[0].Id}", new InventoryRequest(1.0001m, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new { quantity = 1, idempotencyKey = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PutAsJsonAsync("/api/v1/seller/profile", SellerRequest("Invalid phone") with { ContactPhone = null! })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/api/v1/seller/products", new ProductRequest(CategorySeed.All[1].Id, "No slug", null!, "", ProductType.Physical, "no-slug", "Pack", UnitOfMeasure.Pack, 1, "{}"))).StatusCode);
    }

    [Fact]
    public async Task Expired_pickup_code_is_rejected_without_consuming_inventory()
    {
        var x = await Setup(2); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        var p = await ConfirmAndReady(seller, customer, x.Campaign); var code = await Read<IssuedPickupCode>(await customer.PostAsync($"/api/v1/me/pickups/{p.Id}/code", null));
        await using (var scope = f.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Pickups.Where(v => v.Id == p.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.CodeExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/v1/seller/pickups/{p.Id}/collect", new PickupCodeRequest(code.Code))).StatusCode);
        var c = await StoredCampaign(f, x.Campaign); Assert.Equal(0, c.FulfilledQuantity); Assert.Equal(2, c.CommittedQuantity);
    }
}
