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
    public async Task Distinct_customers_race_capacity_then_real_confirmation_races_cancellation()
    {
        var x = await Setup(6); using var f = x.Factory; using var admin = x.Admin; using var seller = x.Seller; using var customer = x.Customer;
        using var other = f.CreateClient(); var user = await Register(other, "capacity-other"); await Login(other, user);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? customer : other)
            .PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations", new ReservationRequest(1, $"distinct-capacity-{i}"))));
        Assert.Equal(6, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var own = (await Read<PageResponse<ReservationResponse>>(await customer.GetAsync("/api/v1/me/reservations"))).Items;
        var theirs = (await Read<PageResponse<ReservationResponse>>(await other.GetAsync("/api/v1/me/reservations"))).Items;
        Assert.Equal(6, own.Count + theirs.Count);
        var owner = own.Count > 0 ? customer : other; var reservation = own.Count > 0 ? own[0] : theirs[0];
        var race = await Task.WhenAll(owner.PostAsync($"/api/v1/me/reservations/{reservation.Id}/cancel", null),
            seller.PostAsJsonAsync($"/api/v1/seller/campaigns/{x.Campaign}/confirm", new DecisionRequest("actual-confirmation-race", "Stock checked")));
        Assert.Equal(HttpStatusCode.Conflict, race[0].StatusCode);
        Assert.True(race[1].StatusCode == HttpStatusCode.OK,
            $"Expected OK, received {race[1].StatusCode}; problem: {await race[1].Content.ReadAsStringAsync()}; safe diagnostics: {string.Join(" | ", f.SafeErrors)}");
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var campaign = await db.Campaigns.AsNoTracking().SingleAsync(c => c.Id == x.Campaign);
        Assert.Equal(CampaignStatus.Confirmed, campaign.Status); Assert.Equal(6, campaign.ReservedQuantity); Assert.Equal(6, campaign.CommittedQuantity);
        Assert.Equal(6, await db.Reservations.Where(r => r.CampaignId == x.Campaign && r.Status == ReservationStatus.Active).SumAsync(r => r.Quantity));
        Assert.Equal(6, await db.Pickups.CountAsync(p => p.CampaignId == x.Campaign));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.EntityId == x.Campaign && a.Action == "InventoryConfirmed"));
    }
}
