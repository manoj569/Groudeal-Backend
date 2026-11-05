using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.Infrastructure;

public sealed partial class Marketplace
{
    private const int PilotJobMaxAttempts = 10;
    private MarketplaceDb JobsDatabase => db;
    private static string JobKey(string stage,Guid id)=>$"pilot-job:{stage}:{id}";
    private IQueryable<Guid> RunnableJobs(IQueryable<Guid> ids,string stage,DateTimeOffset now)=>ids.Where(id=>!db.PilotJobs.Any(j=>j.Stage==stage && j.ItemId==id && (j.State==PilotJobState.Exhausted || j.NextAttemptAtUtc>now)));

    private async Task RecoverExpiredPilotLeases(CancellationToken ct)
    {
        var now=clock.GetUtcNow();
        var ids=await db.PilotJobs.AsNoTracking().Where(j=>j.State==PilotJobState.Running && j.NextAttemptAtUtc<=now).OrderBy(j=>j.NextAttemptAtUtc).Take(50).Select(j=>j.Id).ToListAsync(ct);
        foreach(var id in ids)
        {
            await using var scope=scopes.CreateAsyncScope();var m=scope.ServiceProvider.GetRequiredService<Marketplace>();
            var snapshot=await m.JobsDatabase.PilotJobs.AsNoTracking().SingleAsync(j=>j.Id==id,ct);
            await using var tx=await m.JobsDatabase.Database.BeginTransactionAsync(ct);await m.Advisory(JobKey(snapshot.Stage,snapshot.ItemId),ct);
            var job=await m.JobsDatabase.PilotJobs.SingleAsync(j=>j.Id==id,ct);
            if(job.State!=PilotJobState.Running || job.NextAttemptAtUtc>now){await tx.CommitAsync(ct);continue;}
            // The action may have committed just before process loss. Such items
            // disappear from business due-lists, so recover the ledger independently.
            if(await m.TerminalPilotJob(job.Stage,job.ItemId,now,ct))
            {job.State=PilotJobState.Succeeded;job.CycleAttempts=0;job.ErrorCode=null;job.NextAttemptAtUtc=null;}
            else if(job.CycleAttempts>=PilotJobMaxAttempts || await m.FinancialBudgetExhausted(job.Stage,job.ItemId,ct))
            {job.State=PilotJobState.Exhausted;job.ErrorCode=job.CycleAttempts>=PilotJobMaxAttempts?"worker_lease_exhausted":"provider_operation_exhausted";job.NextAttemptAtUtc=null;}
            else {job.State=PilotJobState.Failed;job.ErrorCode="worker_lease_abandoned";job.NextAttemptAtUtc=now;}
            job.LeaseId=null;m.Audit(null,job.State==PilotJobState.Exhausted?"PilotJobExhausted":"PilotJobLeaseRecovered",job);
            await m.JobsDatabase.SaveChangesAsync(ct);await tx.CommitAsync(ct);workerHealth.Progress(clock.GetUtcNow());
        }
    }

    private async Task RecordProviderCreationFailure(Guid id,int attempts,CancellationToken ct)
    {
        const string stage="payment_reconciliation";
        await using var scope=scopes.CreateAsyncScope();var m=scope.ServiceProvider.GetRequiredService<Marketplace>();
        await using var tx=await m.JobsDatabase.Database.BeginTransactionAsync(ct);await m.Advisory(JobKey(stage,id),ct);
        var job=await m.JobsDatabase.PilotJobs.SingleOrDefaultAsync(j=>j.Stage==stage && j.ItemId==id,ct);
        // Worker-owned calls finish through their fenced lease; direct checkout
        // failures must also have durable visibility and the same finite budget.
        if(job?.State==PilotJobState.Running){await tx.CommitAsync(ct);return;}
        if(job is null){job=new(){Stage=stage,ItemId=id};m.JobsDatabase.PilotJobs.Add(job);}
        job.Attempts++;job.CycleAttempts=Math.Min(10,Math.Max(job.CycleAttempts,attempts));job.LastAttemptAtUtc=clock.GetUtcNow();
        job.State=attempts>=10?PilotJobState.Exhausted:PilotJobState.Failed;job.ErrorCode="provider_order_creation_failed";
        job.NextAttemptAtUtc=attempts>=10?null:clock.GetUtcNow().AddSeconds(45);
        m.Audit(null,attempts>=10?"PilotJobExhausted":"PilotJobFailed",job);await m.JobsDatabase.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }

    public async Task<PageResponse<PilotJobResponse>> Jobs(Actor actor,PageRequest page,PilotJobState? state,CancellationToken ct)
    {
        Admin(actor);Page(page);Require(state is null || Enum.IsDefined(state.Value),"Invalid job state.",400);
        var q=db.PilotJobs.AsNoTracking().Where(j=>state==null || j.State==state);
        return new(await q.OrderByDescending(j=>j.LastAttemptAtUtc).ThenBy(j=>j.Id).Skip(page.Offset).Take(page.PageSize)
            .Select(j=>new PilotJobResponse(j.Id,j.Stage,j.ItemId,j.State,j.Attempts,j.CycleAttempts,j.RetryCount,j.LastAttemptAtUtc,j.NextAttemptAtUtc,j.ErrorCode)).ToListAsync(ct),page.Page,page.PageSize,await q.CountAsync(ct));
    }

    private async Task<Guid?> BeginPilotJob(string stage,Guid id,CancellationToken ct)
    {
        await using var scope=scopes.CreateAsyncScope();var m=scope.ServiceProvider.GetRequiredService<Marketplace>();
        await using var tx=await m.JobsDatabase.Database.BeginTransactionAsync(ct);await m.Advisory(JobKey(stage,id),ct);
        var job=await m.JobsDatabase.PilotJobs.SingleOrDefaultAsync(j=>j.Stage==stage && j.ItemId==id,ct);var now=clock.GetUtcNow();
        if(job is null){job=new(){Stage=stage,ItemId=id};m.JobsDatabase.PilotJobs.Add(job);}
        if(job.State==PilotJobState.Exhausted || job.NextAttemptAtUtc>now){await tx.CommitAsync(ct);return null;}
        if(job.State==PilotJobState.Succeeded && await m.TerminalPilotJob(stage,id,now,ct)){await tx.CommitAsync(ct);return null;}
        var financialExhaustion=await m.FinancialBudgetExhausted(stage,id,ct);
        if(job.CycleAttempts>=PilotJobMaxAttempts || financialExhaustion)
        {
            job.State=PilotJobState.Exhausted;job.ErrorCode=financialExhaustion?"provider_operation_exhausted":"worker_lease_exhausted";job.NextAttemptAtUtc=null;job.LeaseId=null;
            m.Audit(null,"PilotJobExhausted",job);await m.JobsDatabase.SaveChangesAsync(ct);await tx.CommitAsync(ct);return null;
        }
        job.Attempts++;job.CycleAttempts++;job.LastAttemptAtUtc=now;job.NextAttemptAtUtc=now.AddMinutes(5);
        job.LeaseId=Guid.NewGuid();job.State=PilotJobState.Running;
        m.Audit(null,"PilotJobAttemptStarted",job);await m.JobsDatabase.SaveChangesAsync(ct);await tx.CommitAsync(ct);return job.LeaseId;
    }

    private Task<bool> FinancialBudgetExhausted(string stage,Guid id,CancellationToken ct)=>stage switch
    {
        "payment_reconciliation"=>db.PaymentOrders.AnyAsync(o=>o.Id==id && o.CapturedAtUtc==null && o.Attempts>=10,ct),
        "webhook_reconciliation"=>db.PaymentWebhooks.AnyAsync(w=>w.Id==id && w.ProcessedAtUtc==null && w.Attempts>=10,ct),
        "refund_processing"=>db.PaymentRefunds.AnyAsync(r=>r.Id==id && r.State==RefundState.Exhausted,ct),
        "campaign_finalization" or "campaign_opening"=>db.Campaigns.AnyAsync(c=>c.Id==id && c.WorkerFailureCount>=10,ct),
        _=>Task.FromResult(false)
    };

    private Task<bool> TerminalPilotJob(string stage,Guid id,DateTimeOffset now,CancellationToken ct)=>stage switch
    {
        "payment_hold_expiry"=>db.PaymentOrders.AnyAsync(o=>o.Id==id && (o.CapturedAtUtc!=null || o.State==PaymentState.Expired || o.State==PaymentState.Failed),ct),
        "fulfillment_expiry"=>db.OrderFulfillments.AnyAsync(f=>f.Id==id && (f.RedeemedAtUtc!=null || f.State==FulfillmentState.Cancelled),ct),
        "payment_reconciliation"=>db.PaymentOrders.AnyAsync(o=>o.Id==id && o.CapturedAtUtc!=null,ct),
        "webhook_reconciliation"=>db.PaymentWebhooks.AnyAsync(w=>w.Id==id && w.ProcessedAtUtc!=null,ct),
        "refund_processing"=>db.PaymentRefunds.AnyAsync(r=>r.Id==id && r.State==RefundState.Completed,ct),
        "campaign_finalization"=>db.Campaigns.AnyAsync(c=>c.Id==id && c.FinalizedAtUtc!=null,ct),
        "campaign_opening"=>db.Campaigns.AnyAsync(c=>c.Id==id && c.Status!=CampaignStatus.Scheduled,ct),
        _=>Task.FromResult(false)
    };

    private async Task FinishPilotJob(string stage,Guid id,Guid lease,string? error,CancellationToken ct)
    {
        await using var scope=scopes.CreateAsyncScope();var m=scope.ServiceProvider.GetRequiredService<Marketplace>();
        await using var tx=await m.JobsDatabase.Database.BeginTransactionAsync(ct);await m.Advisory(JobKey(stage,id),ct);
        var job=await m.JobsDatabase.PilotJobs.SingleAsync(j=>j.Stage==stage && j.ItemId==id,ct);
        // A late completion cannot overwrite a newer worker's lease or an operator decision.
        if(job.LeaseId!=lease){await tx.CommitAsync(ct);return;}
        var exhausted=error is not null && job.CycleAttempts>=PilotJobMaxAttempts;
        var retry=clock.GetUtcNow().AddSeconds(Math.Min(900,30*Math.Pow(2,job.CycleAttempts-1)));
        if(error is not null)
        {
            if(stage=="payment_reconciliation")await m.JobsDatabase.PaymentOrders.Where(o=>o.Id==id && o.CapturedAtUtc==null).ExecuteUpdateAsync(s=>s.SetProperty(o=>o.Attempts,o=>o.ProviderOrderId==null?o.Attempts:o.Attempts+1).SetProperty(o=>o.RetryAtUtc,retry).SetProperty(o=>o.ErrorCode,error),ct);
            if(stage=="webhook_reconciliation")await m.JobsDatabase.PaymentWebhooks.Where(w=>w.Id==id && w.ProcessedAtUtc==null).ExecuteUpdateAsync(s=>s.SetProperty(w=>w.Attempts,w=>w.Attempts+1).SetProperty(w=>w.RetryAtUtc,retry).SetProperty(w=>w.ErrorCode,error),ct);
            if(stage=="refund_processing")await m.JobsDatabase.PaymentRefunds.Where(r=>r.Id==id && r.State!=RefundState.Completed).ExecuteUpdateAsync(s=>s.SetProperty(r=>r.State,r=>r.Attempts>=10 || exhausted?RefundState.Exhausted:RefundState.Pending).SetProperty(r=>r.RetryAtUtc,retry).SetProperty(r=>r.ErrorCode,error),ct);
            if(stage is "campaign_finalization" or "campaign_opening")await m.JobsDatabase.Campaigns.Where(c=>c.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.WorkerFailureCount,c=>c.WorkerFailureCount+1).SetProperty(c=>c.WorkerRetryAtUtc,retry).SetProperty(c=>c.WorkerErrorCode,error),ct);
        }
        // Existing financial budgets can also exhaust (e.g. ten pending refund polls).
        exhausted |= await m.FinancialBudgetExhausted(stage,id,ct);
        job.State=exhausted?PilotJobState.Exhausted:error is null?PilotJobState.Succeeded:PilotJobState.Failed;
        job.ErrorCode=exhausted?(error??"provider_operation_exhausted"):error;
        job.NextAttemptAtUtc=job.State==PilotJobState.Failed?retry:null;job.LeaseId=null;
        if(error is null && !exhausted)job.CycleAttempts=0;
        m.Audit(null,exhausted?"PilotJobExhausted":error is null?"PilotJobSucceeded":"PilotJobFailed",job);
        await m.JobsDatabase.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }

    private async Task ResetPilotJob(Actor actor,string stage,Guid id,CancellationToken ct)
    {
        await Advisory(JobKey(stage,id),ct);var job=await db.PilotJobs.SingleOrDefaultAsync(j=>j.Stage==stage && j.ItemId==id,ct);
        if(job is null){job=new(){Stage=stage,ItemId=id,State=PilotJobState.Exhausted};db.PilotJobs.Add(job);}
        Require(job.State==PilotJobState.Exhausted,"Only exhausted jobs require operator retry.");
        job.State=PilotJobState.Ready;job.CycleAttempts=0;job.NextAttemptAtUtc=null;job.LeaseId=null;job.ErrorCode=null;job.RetryCount++;
        Audit(actor,"PilotJobRetryApproved",job);
    }

    public async Task RetryJob(Actor actor,Guid id,int expectedRetryCount,CancellationToken ct)
    {
        Admin(actor);var snapshot=await db.PilotJobs.AsNoTracking().SingleOrDefaultAsync(j=>j.Id==id,ct)??throw Missing();
        Require(expectedRetryCount>=0,"Expected retry count must be non-negative.",400);
        if(snapshot.RetryCount>expectedRetryCount)return;
        Require(snapshot.RetryCount==expectedRetryCount,"Retry approval version changed.");
        Require(snapshot.State==PilotJobState.Exhausted,"Only exhausted jobs require operator retry.");
        // A callback or another reconciliation path may already have resolved the
        // business item. Acknowledge that evidence without replaying any side effect.
        if(await TerminalPilotJob(snapshot.Stage,snapshot.ItemId,clock.GetUtcNow(),ct))
        {
            await using var resolved=await db.Database.BeginTransactionAsync(ct);await Advisory(JobKey(snapshot.Stage,snapshot.ItemId),ct);
            var completed=await db.PilotJobs.SingleAsync(j=>j.Id==id,ct);
            if(completed.RetryCount>expectedRetryCount){await resolved.CommitAsync(ct);return;}
            Require(completed.RetryCount==expectedRetryCount,"Retry approval version changed.");
            if(completed.State==PilotJobState.Exhausted){completed.State=PilotJobState.Succeeded;completed.CycleAttempts=0;completed.ErrorCode=null;completed.NextAttemptAtUtc=null;completed.LeaseId=null;completed.RetryCount++;Audit(actor,"PilotJobResolvedFromBusinessState",completed);await db.SaveChangesAsync(ct);}
            await resolved.CommitAsync(ct);return;
        }
        if(snapshot.Stage=="payment_reconciliation"){await RetryPayment(actor,snapshot.ItemId,expectedRetryCount,ct);return;}
        if(snapshot.Stage=="refund_processing"){await RetryRefund(actor,snapshot.ItemId,expectedRetryCount,ct);return;}
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await Advisory(JobKey(snapshot.Stage,snapshot.ItemId),ct);
        var current=await db.PilotJobs.SingleAsync(j=>j.Id==id,ct);
        if(current.RetryCount>expectedRetryCount){await tx.CommitAsync(ct);return;}
        Require(current.RetryCount==expectedRetryCount && current.State==PilotJobState.Exhausted,"Retry approval version changed or job is not exhausted.");
        // Retry only scheduling metadata. Business eligibility is always checked again by the job.
        if(snapshot.Stage=="webhook_reconciliation")
        {
            var w=await db.PaymentWebhooks.SingleAsync(w=>w.Id==snapshot.ItemId,ct);Require(gateway.Available && w.Provider==gateway.Name,"Original payment provider must be configured.");
            await Advisory(JobKey(snapshot.Stage,snapshot.ItemId),ct);await db.Entry(w).ReloadAsync(ct);
            if(w.ProcessedAtUtc is null){w.Attempts=0;w.RetryAtUtc=null;w.ErrorCode=null;}
        }
        else if(snapshot.Stage is "campaign_opening" or "campaign_finalization")
        {
            var(_,c)=await LockFinancialCampaign(snapshot.ItemId,ct);c.WorkerFailureCount=0;c.WorkerRetryAtUtc=null;c.WorkerErrorCode=null;
        }
        else Require(snapshot.Stage is "payment_hold_expiry" or "fulfillment_expiry","Unsupported worker stage.",400);
        await ResetPilotJob(actor,snapshot.Stage,snapshot.ItemId,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
}
