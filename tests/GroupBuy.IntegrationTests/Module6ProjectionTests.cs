using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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
    private sealed class OrdersCounter : DbCommandInterceptor
    {
        public List<(string Sql,NpgsqlParameter[] Parameters)> Commands { get; }=[];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            Commands.Add((command.CommandText,command.Parameters.Cast<NpgsqlParameter>().Select(p=>p.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
    [Fact]
    public async Task Module6_all_status_filters_counts_paging_legacy_and_constant_SQL_batches()
    {
        var x=await Setup(30,true,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var instant=DateTimeOffset.UtcNow.AddMinutes(-10);
        var expected=new Dictionary<ParticipationStatus,Guid>();
        await using(var scope=f.Services.CreateAsyncScope()) {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var template=await db.Campaigns.AsNoTracking().SingleAsync(c=>c.Id==x.Campaign);
            foreach(var status in Enum.GetValues<ParticipationStatus>()) {
                var c=new GroupBuyCampaign { SellerId=template.SellerId,ProductVariantId=template.ProductVariantId,SellerLocationId=template.SellerLocationId,
                    Title="History "+status,ReferenceUnitPrice=100,GroupUnitPrice=80,TargetQuantity=30,CommittedQuantity=30,ReservedQuantity=1,
                    MinimumReservationQuantity=1,MaximumReservationQuantity=30,StartsAtUtc=instant.AddDays(-1),ExpiresAtUtc=instant.AddDays(1),
                    Status=status is ParticipationStatus.Confirmed or ParticipationStatus.Ready ? CampaignStatus.Successful : CampaignStatus.Open,
                    FinalizedAtUtc=status==ParticipationStatus.Ready ? instant : null,
                    UsesTierPricing=true,ServiceArea="unconfigured-history" };
                db.Campaigns.Add(c);
                GroupBuyReservation? r=null;
                if(status is not (ParticipationStatus.PaymentPending or ParticipationStatus.PaymentFailed)) {
                    r=new() { CampaignId=c.Id,CustomerUserId=x.CustomerId,Quantity=1,UnitPriceSnapshot=80,TotalPriceSnapshot=80,
                        CreatedAt=instant,UpdatedAt=instant,IdempotencyKey=Guid.NewGuid().ToString("N"),
                        Status=status==ParticipationStatus.Completed ? ReservationStatus.Completed : status is ParticipationStatus.Cancelled or ParticipationStatus.Refunded ? ReservationStatus.Cancelled : ReservationStatus.Active };
                    db.Reservations.Add(r);
                }
                var o=new PaymentOrder { CustomerId=x.CustomerId,CampaignId=c.Id,ReservationId=r?.Id,Quantity=1,MaximumUnitPrice=80,MaximumAmountMinor=8000,
                    CreatedAt=instant,UpdatedAt=instant,ExpiresAtUtc=instant.AddDays(1),CapturedAtUtc=r==null ? null : instant,
                    IdempotencyKey=Guid.NewGuid().ToString("N"),State=status==ParticipationStatus.PaymentPending ? PaymentState.Authorized :
                        status==ParticipationStatus.PaymentFailed ? PaymentState.Failed : status==ParticipationStatus.Refunded ? PaymentState.Refunded : PaymentState.Captured,
                    RefundedMinor=status==ParticipationStatus.Refunded ? 8000 : 0,FinalAmountMinor=status is ParticipationStatus.Cancelled or ParticipationStatus.Refunded ? 0 : status==ParticipationStatus.Ready ? 8000 : null };
                db.PaymentOrders.Add(o); expected[status]=o.Id;
                if(status is ParticipationStatus.Ready or ParticipationStatus.Completed) {
                    var p=new PickupRecord { CampaignId=c.Id,ReservationId=r!.Id,SellerId=c.SellerId,CustomerUserId=x.CustomerId,
                        DeadlineUtc=instant.AddDays(1),Status=status==ParticipationStatus.Completed ? PickupStatus.Collected : PickupStatus.ReadyForPickup,
                        CollectedAtUtc=status==ParticipationStatus.Completed ? instant : null,CollectedByUserId=status==ParticipationStatus.Completed ? x.CustomerId : null };
                    db.Pickups.Add(p); db.OrderFulfillments.Add(new() { PaymentOrderId=o.Id,PickupId=p.Id,ChoiceConfirmed=true,
                        State=status==ParticipationStatus.Completed ? FulfillmentState.Delivered : FulfillmentState.ReadyForCollection,
                        RedeemedAtUtc=status==ParticipationStatus.Completed ? instant : null });
                }
                if(status==ParticipationStatus.Cancelled) db.PaymentRefunds.Add(new() { PaymentOrderId=o.Id,AmountMinor=8000,State=RefundState.Exhausted,Reason="private internal text" });
                db.PriceTiers.Add(new() { CampaignId=c.Id,Quantity=10,UnitPrice=80 });
            }
            await db.SaveChangesAsync();
        }
        foreach(var pair in expected) {
            var result=await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?status="+pair.Key));
            Assert.Equal(pair.Value,Assert.Single(result.Items).Id); Assert.Equal(1,result.Total); Assert.Equal(8,result.Summary.All);
            Assert.All(result.Summary.Statuses.Values,n=>Assert.Equal(1,n));
        }
        var first=await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?pageSize=3"));
        var repeat=await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?pageSize=3"));
        var second=await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?pageSize=3&page=2"));
        Assert.Equal(first.Items.Select(r=>r.Id),repeat.Items.Select(r=>r.Id)); Assert.True(first.HasMore);
        Assert.Empty(first.Items.Select(r=>r.Id).Intersect(second.Items.Select(r=>r.Id)));
        Assert.Empty((await Read<MyOrdersPage>(await customer.GetAsync("/api/v1/me/orders?page=100000"))).Items);
        var cancelled=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{expected[ParticipationStatus.Cancelled]}"));
        Assert.Equal("requires_attention",cancelled.Order.Commercial.RefundStatus); Assert.Null(cancelled.Cancellation.CancelledAtUtc);
        Assert.Equal("refund",Assert.Single(cancelled.Refunds).ReasonCode);
        var counter=new OrdersCounter();
        await using var counted=new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(fixture.Connection).AddInterceptors(counter).Options);
        await using var serviceScope=f.Services.CreateAsyncScope();
        var service=ActivatorUtilities.CreateInstance<Marketplace>(serviceScope.ServiceProvider,counted);
        var actor=new Actor(x.CustomerId,false,"module6-query-test");
        await service.MyOrders(actor,new(PageSize:1),default); Assert.Equal(3,counter.Commands.Count); counter.Commands.Clear();
        await service.MyOrders(actor,new(PageSize:100),default); Assert.Equal(3,counter.Commands.Count);
        var pageSql=counter.Commands.Single(c=>c.Sql.Contains("LIMIT"));
        foreach(var secret in new[]{"AddressCiphertext","TokenHash","BackupCodeHash","ProviderOrderId","DecisionReason"}) Assert.DoesNotContain(secret,pageSql.Sql);
        await using(var connection=new NpgsqlConnection(fixture.Connection)) {
            await connection.OpenAsync(); await using var explain=new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "+pageSql.Sql,connection);
            explain.Parameters.AddRange(pageSql.Parameters); var plan=(await explain.ExecuteScalarAsync())!.ToString()!;
            Assert.Contains("Limit",plan); await File.WriteAllTextAsync(Path.Combine(DatabaseFixture.FindRepositoryRoot(),".local","module6-query-plan.json"),plan);
        }
        counter.Commands.Clear(); await service.MyOrder(actor,expected[ParticipationStatus.Completed],default); Assert.Equal(3,counter.Commands.Count);
    }
    [Fact]
    public async Task Module6_legacy_reservation_has_no_invented_payment_and_reuses_cancellation()
    {
        var x=await Setup(4,false,true); using var f=x.Factory; using var customer=x.Customer; using var seller=x.Seller; using var admin=x.Admin;
        var reserved=await Read<ReservationResponse>(await customer.PostAsJsonAsync($"/api/v1/group-buys/{x.Campaign}/reservations",new ReservationRequest(1,"module6-legacy")));
        var detail=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{reserved.Id}"));
        Assert.Equal("reservation",detail.Order.Source); Assert.Equal(ParticipationStatus.Active,detail.Order.Status);
        Assert.Equal("not_tracked",detail.Order.PaymentStatus); Assert.Null(detail.Order.Commercial.PaidAmountMinor);
        Assert.Null(detail.Order.Commercial.MaximumInitialAmountMinor); Assert.True(detail.Cancellation.CanCancel);
        await Read<ReservationResponse>(await customer.PostAsync($"/api/v1/me/reservations/{reserved.Id}/cancel",null));
        detail=await Read<MyOrderDetail>(await customer.GetAsync($"/api/v1/me/orders/{reserved.Id}"));
        Assert.Equal(ParticipationStatus.Cancelled,detail.Order.Status); Assert.NotNull(detail.Cancellation.CancelledAtUtc);
    }
}

