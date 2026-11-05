using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact]
    [Trait("Sprint5", "WorkerRecovery")]
    public async Task Expired_campaign_is_processed_once_after_API_host_restart()
    {
        var setup = await Setup();
        var campaignId = Guid.Empty;
        DateTimeOffset expiry;
        var factory = setup.Factory;
        try
        {
            Guid variantId;
            Guid locationId;
            long inventoryVersion;
            decimal availableQuantity;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
                var source = await db.Campaigns.AsNoTracking().Where(c => c.Id == setup.Campaign)
                    .Select(c => new { c.ProductVariantId, c.SellerLocationId }).SingleAsync();
                variantId = source.ProductVariantId;
                locationId = source.SellerLocationId;
                var inventory = await db.Inventories.AsNoTracking().Where(i => i.ProductVariantId == variantId)
                    .Select(i => new { i.Version, i.AvailableQuantity }).SingleAsync();
                inventoryVersion = inventory.Version;
                availableQuantity = inventory.AvailableQuantity;
            }

            await Read<InventoryResponse>(await setup.Seller.PutAsJsonAsync($"/api/v1/seller/inventory/{variantId}",
                new InventoryRequest(availableQuantity + 10, inventoryVersion)));

            expiry = DateTimeOffset.UtcNow.AddMinutes(2);
            var created = await Read<CampaignResponse>(await setup.Seller.PostAsJsonAsync("/api/v1/seller/group-buys",
                new CampaignRequest(variantId, locationId, "Worker restart recovery", "Disposable integration worker scenario",
                    100, 80, 10, 1, 10, DateTimeOffset.UtcNow.AddMinutes(-1), expiry)));
            campaignId = created.Id;
            Assert.Equal(CampaignStatus.Draft, created.Status);
            var submitted = await Read<CampaignResponse>(await setup.Seller.PostAsync($"/api/v1/seller/group-buys/{campaignId}/submit", null));
            Assert.Equal(CampaignStatus.PendingApproval, submitted.Status);
            var approved = await Read<CampaignResponse>(await setup.Admin.PostAsJsonAsync($"/api/v1/admin/group-buys/{campaignId}/approve", new ReviewRequest(null)));
            Assert.Equal(CampaignStatus.Open, approved.Status);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
                var persisted = await db.Campaigns.AsNoTracking().Where(c => c.Id == campaignId)
                    .Select(c => new { c.Status, c.ExpiresAtUtc, c.WorkerFailureCount, c.CommittedQuantity, c.ReservedQuantity }).SingleAsync();
                Assert.Equal(CampaignStatus.Open, persisted.Status);
                Assert.Equal(0, persisted.WorkerFailureCount);
                Assert.Equal(10, persisted.CommittedQuantity);
                Assert.Equal(0, persisted.ReservedQuantity);
                Assert.True(persisted.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(30));
                expiry = persisted.ExpiresAtUtc;
            }
        }
        catch
        {
            setup.Admin.Dispose(); setup.Seller.Dispose(); setup.Customer.Dispose(); factory.Dispose();
            throw;
        }

        setup.Admin.Dispose(); setup.Seller.Dispose(); setup.Customer.Dispose();
        await factory.DisposeAsync();
        var untilDue = expiry.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (untilDue > TimeSpan.Zero) await Task.Delay(untilDue);

        using var restartedFactory = new ApiFactory(fixture.Connection);
        using var client = restartedFactory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var workerHealth = restartedFactory.Services.GetRequiredService<WorkerHealthState>();
        var workerDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var workerSnapshot = workerHealth.Read();
        do
        {
            workerSnapshot = workerHealth.Read();
            if (workerSnapshot.LastCompletedAtUtc is not null && workerSnapshot.LastBatchErrorType is null) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        } while (DateTimeOffset.UtcNow < workerDeadline);
        Assert.True(workerSnapshot.LastCompletedAtUtc is not null && workerSnapshot.LastBatchErrorType is null,
            $"Worker did not complete a batch after restart; safe error type: {workerSnapshot.LastBatchErrorType ?? "none"}.");

        async Task<(CampaignStatus Status, decimal Committed, decimal Reserved, int Failures, DateTimeOffset? RetryAt, string? ErrorCode, uint Version, int ExpiryAuditCount)> ReadState()
        {
            await using var scope = restartedFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var campaign = await db.Campaigns.AsNoTracking().Where(c => c.Id == campaignId)
                .Select(c => new { c.Status, c.CommittedQuantity, c.ReservedQuantity, c.WorkerFailureCount, c.WorkerRetryAtUtc, c.WorkerErrorCode, c.RowVersion }).SingleAsync();
            var audits = await db.AuditLogs.CountAsync(a => a.EntityId == campaignId && a.Action == "CampaignExpired");
            return (campaign.Status, campaign.CommittedQuantity, campaign.ReservedQuantity, campaign.WorkerFailureCount,
                campaign.WorkerRetryAtUtc, campaign.WorkerErrorCode, campaign.RowVersion, audits);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var afterRestart = await ReadState();
        while (afterRestart.Status != CampaignStatus.Expired && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            afterRestart = await ReadState();
        }

        Assert.Equal(CampaignStatus.Expired, afterRestart.Status);
        Assert.Equal(0, afterRestart.Committed);
        Assert.Equal(0, afterRestart.Reserved);
        Assert.Equal(0, afterRestart.Failures);
        Assert.Null(afterRestart.RetryAt);
        Assert.Null(afterRestart.ErrorCode);
        Assert.Equal(1, afterRestart.ExpiryAuditCount);

        await Task.Delay(TimeSpan.FromSeconds(20));
        var stable = await ReadState();
        Assert.Equal(CampaignStatus.Expired, stable.Status);
        Assert.Equal(afterRestart.Version, stable.Version);
        Assert.Equal(afterRestart.ExpiryAuditCount, stable.ExpiryAuditCount);
        Assert.Equal(0, stable.Failures);
        Assert.Null(stable.RetryAt);
        Assert.Null(stable.ErrorCode);
    }
}
