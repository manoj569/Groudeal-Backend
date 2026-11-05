using System.Net;
using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GroupBuy.IntegrationTests;

public partial class BackendTests
{
    [Fact]
    public async Task Pilot_admin_job_routes_require_completed_mfa()
    {
        using var f=new ApiFactory(fixture.Connection,disableWorker:true);using var client=f.CreateClient();
        var enrollment=await StartEnrollment(f,client);client.DefaultRequestHeaders.Authorization=new("Bearer",enrollment.Challenge.MfaChallenge);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/mvp/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync($"/api/v1/mvp/admin/jobs/{Guid.NewGuid()}/retry?expectedRetryCount=0",null)).StatusCode);
        client.DefaultRequestHeaders.Authorization=null;
        var token=await Read<MfaTokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/verify",new MfaVerifyRequest(enrollment.Challenge.MfaChallenge!,await UnusedTotp(f,enrollment.User.Id,enrollment.Key))));
        client.DefaultRequestHeaders.Authorization=new("Bearer",token.AccessToken);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/v1/mvp/admin/jobs")).StatusCode);
    }
    [Fact]
    public async Task Pilot_retry_migration_retains_legacy_failure_evidence_and_refuses_destructive_rollback()
    {
        var name="pilot_retry_upgrade_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(fixture.Connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE DATABASE {name}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            var connection=new NpgsqlConnectionStringBuilder(fixture.Connection){Database=name,Pooling=false}.ConnectionString;
            await using var db=new MarketplaceDb(new DbContextOptionsBuilder<MarketplaceDb>().UseNpgsql(connection).Options);
            var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261104233113_TrackPaidPickupPolicy");
            var eventId=Guid.NewGuid().ToString("N");var id=Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO payment_webhooks (\"Id\",\"Provider\",\"EventId\",\"PayloadHash\",\"ProviderOrderId\",\"ReceivedAtUtc\",\"Attempts\",\"ErrorCode\") VALUES ({id},'Sandbox',{eventId},{new string('A',64)},'sandbox_preserved',{DateTimeOffset.UtcNow},10,'reconciliation_required')");
            await migrator.MigrateAsync();var job=await db.PilotJobs.SingleAsync(j=>j.ItemId==id);
            Assert.Equal(PilotJobState.Exhausted,job.State);Assert.Equal(10,job.Attempts);Assert.Equal(10,job.CycleAttempts);Assert.Null(job.LastAttemptAtUtc);
            var webhook=await db.PaymentWebhooks.SingleAsync(w=>w.Id==id);Assert.Equal(eventId,webhook.EventId);Assert.Equal("sandbox_preserved",webhook.ProviderOrderId);Assert.Null(webhook.ProcessedAtUtc);
            var error=await Assert.ThrowsAsync<PostgresException>(()=>migrator.MigrateAsync("20261104233113_TrackPaidPickupPolicy"));Assert.Equal("P0001",error.SqlState);
            Assert.Equal(1,await db.PilotJobs.CountAsync());
        }
        finally{await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync();}
    }
    private static async Task RunPilotWorker(ApiFactory factory)
    {
        await using var scope=factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Process(default);
    }
    private static async Task<PilotJob> ReadJob(ApiFactory factory,string stage,Guid item)
    {
        await using var scope=factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PilotJobs.AsNoTracking().SingleAsync(j=>j.Stage==stage && j.ItemId==item);
    }
    private static async Task MakeJobDue(ApiFactory factory,params Guid[] items)
    {
        // Explicitly model the next scheduler tick after backoff; no wall-clock sleeps.
        await using var scope=factory.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        await db.PilotJobs.Where(j=>items.Contains(j.ItemId)).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.NextAttemptAtUtc,(DateTimeOffset?)null));
        await db.PaymentOrders.Where(o=>items.Contains(o.Id)).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.RetryAtUtc,(DateTimeOffset?)null));
        await db.PaymentWebhooks.Where(w=>items.Contains(w.Id)).ExecuteUpdateAsync(s=>s.SetProperty(w=>w.RetryAtUtc,(DateTimeOffset?)null));
        await db.PaymentRefunds.Where(r=>items.Contains(r.Id)).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.RetryAtUtc,(DateTimeOffset?)null));
    }
    private static async Task<string> InstallWorkerFailure(ApiFactory factory,string table,Guid item,string condition)
    {
        var name="fail_pilot_"+Guid.NewGuid().ToString("N");
        Assert.Contains(table,new[]{"payment_orders","order_fulfillments","payment_refunds"});
        await using var scope=factory.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        // All identifiers/conditions originate in this test; item/name are generated UUIDs.
        var ddl=$$"""
            CREATE FUNCTION {{name}}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'Synthetic worker storage failure'; END $body$;
            CREATE TRIGGER {{name}} BEFORE UPDATE ON {{table}} FOR EACH ROW
            WHEN (OLD."Id" = '{{item}}'::uuid AND ({{condition}})) EXECUTE FUNCTION {{name}}();
            """;
        await db.Database.ExecuteSqlRawAsync(ddl);
        return name;
    }
    private static async Task RemoveWorkerFailure(ApiFactory factory,string table,string name)
    {
        await using var scope=factory.Services.CreateAsyncScope();
        Assert.Contains(table,new[]{"payment_orders","order_fulfillments","payment_refunds"});
        Assert.True(name.StartsWith("fail_pilot_",StringComparison.Ordinal) && name.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'));
        var ddl=$"DROP TRIGGER IF EXISTS {name} ON {table}; DROP FUNCTION IF EXISTS {name}();";
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().Database.ExecuteSqlRawAsync(ddl);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Pilot_expiry_retries_survive_host_restart_exhaust_and_recover_once(bool fulfillment)
    {
        var x=await Setup(2,true,disableWorker:true);using var original=x.Factory;using var customer=x.Customer;using var seller=x.Seller;using var admin=x.Admin;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,fulfillment?2:1)));
        Guid item=order.Id;var stage=fulfillment?"fulfillment_expiry":"payment_hold_expiry";var table=fulfillment?"order_fulfillments":"payment_orders";
        if(fulfillment)
        {
            await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
            await Read<TierCampaignResponse>(await seller.PostAsync($"/api/v1/mvp/seller/campaigns/{x.Campaign}/finalize",null));
            await RunPilotWorker(original);
        }
        await using(var scope=original.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            if(fulfillment)
            {
                var f=await db.OrderFulfillments.SingleAsync(f=>f.PaymentOrderId==order.Id);item=f.Id;
                await db.Pickups.Where(p=>p.Id==f.PickupId).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.DeadlineUtc,DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            else await db.PaymentOrders.Where(o=>o.Id==order.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.ExpiresAtUtc,DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var condition=fulfillment?$"NEW.\"State\" = {(int)FulfillmentState.Cancelled}":$"NEW.\"State\" = {(int)PaymentState.Expired}";
        var trigger=await InstallWorkerFailure(original,table,item,condition);
        var settings=original.Services.GetRequiredService<IConfiguration>().AsEnumerable().Where(p=>p.Key.StartsWith("Pilot:") || p.Key.StartsWith("Payments:") || p.Key.StartsWith("Jwt:")).ToDictionary(p=>p.Key,p=>p.Value);
        var adminToken=admin.DefaultRequestHeaders.Authorization;var customerToken=customer.DefaultRequestHeaders.Authorization;
        using var restarted=new ApiFactory(fixture.Connection,overrides:settings,disableWorker:true);
        try
        {
            await RunPilotWorker(original);var first=await ReadJob(original,stage,item);
            Assert.Equal(PilotJobState.Failed,first.State);Assert.Equal(1,first.Attempts);Assert.Equal(1,first.CycleAttempts);
            Assert.NotNull(first.LastAttemptAtUtc);Assert.True(first.NextAttemptAtUtc>first.LastAttemptAtUtc);
            await using(var delay=original.Services.CreateAsyncScope())await delay.ServiceProvider.GetRequiredService<MarketplaceDb>().PilotJobs.Where(j=>j.Id==first.Id).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.NextAttemptAtUtc,DateTimeOffset.UtcNow.AddYears(1)));
            await RunPilotWorker(original);Assert.Equal(1,(await ReadJob(original,stage,item)).Attempts);
            customer.Dispose();seller.Dispose();admin.Dispose();await original.DisposeAsync();
            using var operatorClient=restarted.CreateClient();operatorClient.DefaultRequestHeaders.Authorization=adminToken;
            Assert.Equal(first.Attempts,(await ReadJob(restarted,stage,item)).Attempts);
            for(var attempt=2;attempt<=10;attempt++){await MakeJobDue(restarted,item);await RunPilotWorker(restarted);Assert.Equal(attempt,(await ReadJob(restarted,stage,item)).Attempts);}
            var exhausted=await ReadJob(restarted,stage,item);Assert.Equal(PilotJobState.Exhausted,exhausted.State);Assert.Equal(10,exhausted.CycleAttempts);Assert.Null(exhausted.NextAttemptAtUtc);
            await RunPilotWorker(restarted);Assert.Equal(10,(await ReadJob(restarted,stage,item)).Attempts);
            var visible=await Read<PageResponse<PilotJobResponse>>(await operatorClient.GetAsync("/api/v1/mvp/admin/jobs?state=Exhausted"));Assert.Contains(visible.Items,j=>j.Id==exhausted.Id && j.ErrorCode!=null);
            using var outsider=restarted.CreateClient();outsider.DefaultRequestHeaders.Authorization=customerToken;
            Assert.Equal(HttpStatusCode.Forbidden,(await outsider.PostAsync($"/api/v1/mvp/admin/jobs/{exhausted.Id}/retry?expectedRetryCount=0",null)).StatusCode);
            await RemoveWorkerFailure(restarted,table,trigger);
            for(var i=0;i<2;i++)Assert.Equal(HttpStatusCode.NoContent,(await operatorClient.PostAsync($"/api/v1/mvp/admin/jobs/{exhausted.Id}/retry?expectedRetryCount=0",null)).StatusCode);
            var approved=await ReadJob(restarted,stage,item);Assert.Equal(10,approved.Attempts);Assert.Equal(0,approved.CycleAttempts);Assert.Equal(1,approved.RetryCount);
            // Model delivery of the same approval after a later cycle exhausted.
            // It must not reset that new budget; a fresh observed version is required.
            await using(var later=restarted.Services.CreateAsyncScope())await later.ServiceProvider.GetRequiredService<MarketplaceDb>().PilotJobs.Where(j=>j.Id==approved.Id).ExecuteUpdateAsync(s=>s.SetProperty(j=>j.State,PilotJobState.Exhausted).SetProperty(j=>j.CycleAttempts,10));
            Assert.Equal(HttpStatusCode.NoContent,(await operatorClient.PostAsync($"/api/v1/mvp/admin/jobs/{exhausted.Id}/retry?expectedRetryCount=0",null)).StatusCode);
            Assert.Equal(PilotJobState.Exhausted,(await ReadJob(restarted,stage,item)).State);
            Assert.Equal(HttpStatusCode.Conflict,(await operatorClient.PostAsync($"/api/v1/mvp/admin/jobs/{exhausted.Id}/retry?expectedRetryCount=2",null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,(await operatorClient.PostAsync($"/api/v1/mvp/admin/jobs/{exhausted.Id}/retry?expectedRetryCount=1",null)).StatusCode);
            await Task.WhenAll(RunPilotWorker(restarted),RunPilotWorker(restarted));await RunPilotWorker(restarted);
            var success=await ReadJob(restarted,stage,item);Assert.Equal(PilotJobState.Succeeded,success.State);Assert.Equal(11,success.Attempts);Assert.Null(success.ErrorCode);
            await using var check=restarted.Services.CreateAsyncScope();var db=check.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.Equal(1,await db.AuditLogs.CountAsync(a=>a.EntityId==item && a.Action==(fulfillment?"FulfillmentExpired":"PaymentHoldExpired")));
            Assert.Equal(2,await db.AuditLogs.CountAsync(a=>a.EntityId==success.Id && a.Action=="PilotJobRetryApproved"));
            if(fulfillment){Assert.Equal(1,await db.PaymentRefunds.CountAsync(r=>r.PaymentOrderId==order.Id && r.Reason=="fulfillment_expired"));Assert.Equal(order.MaximumAmountMinor,(await db.PaymentOrders.SingleAsync(o=>o.Id==order.Id)).RefundedMinor);}
            else Assert.Null((await db.PaymentOrders.SingleAsync(o=>o.Id==order.Id)).ReservationId);
        }
        finally {await RemoveWorkerFailure(restarted,table,trigger);}
    }

    [Fact]
    public async Task Pilot_webhook_and_reconciliation_exhaustion_retain_evidence_and_retry_one_capture()
    {
        var x=await Setup(4,true,disableWorker:true);using var f=x.Factory;using var admin=x.Admin;using var customer=x.Customer;using var seller=x.Seller;
        var checkout=Checkout(x.Campaign,1);var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",checkout));
        var originalProviderOrder=order.ProviderOrderId;
        await using(var exhausted=f.Services.CreateAsyncScope())await exhausted.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentOrders.Where(o=>o.Id==order.Id).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.ProviderOrderId,(string?)null).SetProperty(o=>o.State,PaymentState.Creating).SetProperty(o=>o.Attempts,10).SetProperty(o=>o.RetryAtUtc,(DateTimeOffset?)null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",checkout)).StatusCode);
        Assert.Equal(PilotJobState.Exhausted,(await ReadJob(f,"payment_reconciliation",order.Id)).State);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsync($"/api/v1/mvp/admin/orders/{order.Id}/retry?expectedRetryCount=0",null)).StatusCode);
        order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",checkout));Assert.Equal(originalProviderOrder,order.ProviderOrderId);
        await using(var recovered=f.Services.CreateAsyncScope())Assert.Equal(1,await recovered.ServiceProvider.GetRequiredService<MarketplaceDb>().SandboxReceipts.CountAsync(r=>r.LocalOrderId==order.Id));
        Guid webhook;
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var gateway=(SandboxPaymentGateway)scope.ServiceProvider.GetRequiredService<IPaymentGateway>();var e=await gateway.Simulate(order.ProviderOrderId!,"captured",default);
            await scope.ServiceProvider.GetRequiredService<IPhaseOne>().Webhook(e.Raw,e.Signature,e.EventId,default);
            webhook=await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentWebhooks.Where(w=>w.EventId==e.EventId).Select(w=>w.Id).SingleAsync();
        }
        var trigger=await InstallWorkerFailure(f,"payment_orders",order.Id,"NEW.\"CapturedAtUtc\" IS NOT NULL AND OLD.\"CapturedAtUtc\" IS NULL");
        try
        {
            for(var i=0;i<10;i++){await MakeJobDue(f,order.Id,webhook);await RunPilotWorker(f);}
            var paymentJob=await ReadJob(f,"payment_reconciliation",order.Id);var webhookJob=await ReadJob(f,"webhook_reconciliation",webhook);
            Assert.Equal(PilotJobState.Exhausted,paymentJob.State);Assert.Equal(PilotJobState.Exhausted,webhookJob.State);
            await RunPilotWorker(f);Assert.Equal(10,(await ReadJob(f,"webhook_reconciliation",webhook)).Attempts);
            await using(var scope=f.Services.CreateAsyncScope())
            {
                var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();var w=await db.PaymentWebhooks.SingleAsync(w=>w.Id==webhook);
                Assert.Equal(order.ProviderOrderId,w.ProviderOrderId);Assert.NotEmpty(w.EventId);Assert.Equal(64,w.PayloadHash.Length);Assert.Null(w.ProcessedAtUtc);Assert.Equal(10,w.Attempts);
                Assert.Equal(0,(await db.Campaigns.SingleAsync(c=>c.Id==x.Campaign)).ReservedQuantity);
            }
            await RemoveWorkerFailure(f,"payment_orders",trigger);
            foreach(var job in new[]{paymentJob,webhookJob})Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsync($"/api/v1/mvp/admin/jobs/{job.Id}/retry?expectedRetryCount={job.RetryCount}",null)).StatusCode);
            await Task.WhenAll(RunPilotWorker(f),RunPilotWorker(f));await RunPilotWorker(f);
            await using var check=f.Services.CreateAsyncScope();var data=check.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.NotNull((await data.PaymentWebhooks.SingleAsync(w=>w.Id==webhook)).ProcessedAtUtc);
            Assert.Equal(1,await data.Reservations.CountAsync(r=>r.CampaignId==x.Campaign));Assert.Equal(1,(await data.Campaigns.SingleAsync(c=>c.Id==x.Campaign)).ReservedQuantity);
        }
        finally{await RemoveWorkerFailure(f,"payment_orders",trigger);}
    }

    [Fact]
    public async Task Pilot_refund_response_loss_exhausts_and_manual_retry_reuses_the_original_provider_operation()
    {
        var x=await Setup(2,true,disableWorker:true);using var f=x.Factory;using var admin=x.Admin;using var customer=x.Customer;using var seller=x.Seller;
        var order=await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync("/api/v1/mvp/customer/orders",Checkout(x.Campaign,1)));
        await Read<PaymentOrderResponse>(await customer.PostAsJsonAsync($"/api/v1/mvp/customer/orders/{order.Id}/sandbox?success=true",new{}));
        Assert.Equal(HttpStatusCode.NoContent,(await customer.PostAsync($"/api/v1/mvp/customer/orders/{order.Id}/cancel",null)).StatusCode);
        Guid refund;await using(var scope=f.Services.CreateAsyncScope())refund=await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentRefunds.Where(r=>r.PaymentOrderId==order.Id).Select(r=>r.Id).SingleAsync();
        var trigger=await InstallWorkerFailure(f,"payment_refunds",refund,$"NEW.\"State\" = {(int)RefundState.Completed}");
        try
        {
            for(var i=0;i<10;i++){await MakeJobDue(f,refund);await RunPilotWorker(f);}
            var job=await ReadJob(f,"refund_processing",refund);Assert.Equal(PilotJobState.Exhausted,job.State);
            await using(var scope=f.Services.CreateAsyncScope())
            {
                var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();Assert.Equal(RefundState.Exhausted,(await db.PaymentRefunds.SingleAsync(r=>r.Id==refund)).State);
                Assert.Equal(1,await db.SandboxRefundReceipts.CountAsync(r=>r.RefundId==refund));Assert.Equal(0,(await db.PaymentOrders.SingleAsync(o=>o.Id==order.Id)).RefundedMinor);
            }
            await RemoveWorkerFailure(f,"payment_refunds",trigger);
            Assert.Equal(HttpStatusCode.Forbidden,(await customer.PostAsync($"/api/v1/mvp/admin/refunds/{refund}/retry?expectedRetryCount=0",null)).StatusCode);
            for(var i=0;i<2;i++)Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsync($"/api/v1/mvp/admin/refunds/{refund}/retry?expectedRetryCount=0",null)).StatusCode);
            await Task.WhenAll(RunPilotWorker(f),RunPilotWorker(f));await RunPilotWorker(f);
            await using var check=f.Services.CreateAsyncScope();var data=check.ServiceProvider.GetRequiredService<MarketplaceDb>();
            Assert.Equal(1,await data.SandboxRefundReceipts.CountAsync(r=>r.RefundId==refund));Assert.Equal(order.MaximumAmountMinor,(await data.PaymentOrders.SingleAsync(o=>o.Id==order.Id)).RefundedMinor);
            Assert.Equal(1,(await ReadJob(f,"refund_processing",refund)).RetryCount);
        }
        finally{await RemoveWorkerFailure(f,"payment_refunds",trigger);}
    }

    [Theory][InlineData(10,0,"worker_lease_exhausted",false)][InlineData(1,10,"provider_operation_exhausted",false)][InlineData(10,0,null,true)]
    public async Task Pilot_abandoned_webhook_lease_exhausts_without_losing_the_event(int cycleAttempts,int legacyAttempts,string? expectedError,bool completed)
    {
        using var f=new ApiFactory(fixture.Connection,overrides:new(){["Pilot:Enabled"]="true",["Pilot:ServiceAreas:0:Id"]="synthetic-area",["Pilot:ServiceAreas:0:Name"]="Synthetic",["Pilot:ServiceAreas:0:PostalCodes:0"]="TEST01",["Payments:Enabled"]="true",["Payments:Provider"]="Sandbox",["Payments:SandboxEnabled"]="true",["Payments:SandboxWebhookSecret"]=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))},disableWorker:true);
        using var client=f.CreateClient();var w=new PaymentWebhook{Provider="Sandbox",EventId=Guid.NewGuid().ToString("N"),ProviderOrderId="sandbox_unmapped",PayloadHash=new string('A',64),ReceivedAtUtc=DateTimeOffset.UtcNow,Attempts=legacyAttempts,ProcessedAtUtc=completed?DateTimeOffset.UtcNow:null};
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();db.PaymentWebhooks.Add(w);
            db.PilotJobs.Add(new(){Stage="webhook_reconciliation",ItemId=w.Id,State=PilotJobState.Running,Attempts=cycleAttempts,CycleAttempts=cycleAttempts,LeaseId=Guid.NewGuid(),LastAttemptAtUtc=DateTimeOffset.UtcNow.AddMinutes(-6),NextAttemptAtUtc=DateTimeOffset.UtcNow.AddMinutes(-1)});await db.SaveChangesAsync();
        }
        var health=f.Services.GetRequiredService<WorkerHealthState>();health.Started(DateTimeOffset.UtcNow.AddMinutes(-2));
        await RunPilotWorker(f);await RunPilotWorker(f);var job=await ReadJob(f,"webhook_reconciliation",w.Id);
        Assert.True(health.Read().LastProgressAtUtc>health.Read().LastStartedAtUtc);
        Assert.Equal(completed?PilotJobState.Succeeded:PilotJobState.Exhausted,job.State);Assert.Equal(cycleAttempts,job.Attempts);Assert.Equal(expectedError,job.ErrorCode);
        await using var check=f.Services.CreateAsyncScope();var persisted=await check.ServiceProvider.GetRequiredService<MarketplaceDb>().PaymentWebhooks.SingleAsync(x=>x.Id==w.Id);Assert.Equal(completed,persisted.ProcessedAtUtc is not null);
    }
}
