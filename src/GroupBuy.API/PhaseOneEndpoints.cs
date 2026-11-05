using System.Security.Claims;
using GroupBuy.Application;
using GroupBuy.Domain;
namespace GroupBuy.API;
public static class PhaseOneEndpoints
{
    private static Actor Actor(HttpContext c)=>new(Guid.Parse(c.User.FindFirstValue("sub")!),c.User.IsInRole("Admin"),c.TraceIdentifier);
    private static PageRequest Page(int? page,int? pageSize)=>new(page??1,pageSize??20);
    public static void MapPhaseOne(this WebApplication app)
    {
        var api=app.MapGroup("/api/v1/mvp").WithTags("Phase one pilot");
        api.MapGet("/options",(IPhaseOne s)=>s.Options());
        api.MapGet("/campaigns",(int? page,int? pageSize,IPhaseOne s,CancellationToken ct)=>s.Campaigns(Page(page,pageSize),ct));
        api.MapGet("/campaigns/{id:guid}",(Guid id,IPhaseOne s,CancellationToken ct)=>s.Campaign(id,ct));
        api.MapPost("/payments/webhook",async(HttpRequest r,IPhaseOne s,CancellationToken ct)=>{using var stream=new MemoryStream();await r.Body.CopyToAsync(stream,ct);await s.Webhook(stream.ToArray(),r.Headers["X-Razorpay-Signature"].ToString(),r.Headers["X-Razorpay-Event-Id"].ToString(),ct);return Results.Ok();});
        var customer=api.MapGroup("/customer").RequireAuthorization("Customer");
        customer.MapGet("/interests",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Interests(Actor(c),Page(page,pageSize),ct));
        customer.MapPost("/interests",(InterestRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Interest(Actor(c),r,false,ct));
        customer.MapPost("/interests/withdraw",(InterestRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Interest(Actor(c),r,true,ct));
        customer.MapPost("/orders",(PaymentOrderRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Join(Actor(c),r,ct)).RequireRateLimiting("checkout");
        customer.MapGet("/orders",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Orders(Actor(c),Page(page,pageSize),false,ct));
        customer.MapGet("/orders/{id:guid}",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Order(Actor(c),id,ct));
        customer.MapPost("/orders/{id:guid}/confirm",(Guid id,CheckoutConfirmation r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.ConfirmPayment(Actor(c),id,r,ct));
        customer.MapPost("/orders/{id:guid}/sandbox",(Guid id,bool success,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.SandboxPay(Actor(c),id,success,ct));
        customer.MapPost("/orders/{id:guid}/cancel",async(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>{await s.Cancel(Actor(c),id,ct);return Results.NoContent();});
        customer.MapGet("/fulfillments",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Fulfillments(Actor(c),"customer",Page(page,pageSize),null,null,null,null,ct));
        customer.MapGet("/fulfillments/{id:guid}",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Fulfillment(Actor(c),id,"customer",ct));
        customer.MapPost("/fulfillments/{id:guid}/confirm-choice",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.ConfirmChoice(Actor(c),id,ct));
        customer.MapPost("/fulfillments/{id:guid}/pass",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.IssuePass(Actor(c),id,ct)).RequireRateLimiting("pickup");
        var seller=api.MapGroup("/seller").RequireAuthorization("Seller");
        seller.MapGet("/demand",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Demand(Actor(c),true,Page(page,pageSize),ct));
        seller.MapPut("/campaigns/{id:guid}/tiers",(Guid id,TierConfigurationRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.ConfigureTiers(Actor(c),id,r,ct));
        seller.MapPost("/campaigns/{id:guid}/finalize",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Finalize(Actor(c),id,ct));
        seller.MapPost("/campaigns/{id:guid}/rescue",(Guid id,RescueRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Rescue(Actor(c),id,r,ct));
        MapFulfillment(seller,"seller");
        var admin=api.MapGroup("/admin").RequireAuthorization("Admin").RequireRateLimiting("admin");
        admin.MapGet("/jobs",(int? page,int? pageSize,PilotJobState? state,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Jobs(Actor(c),Page(page,pageSize),state,ct));
        admin.MapPost("/jobs/{id:guid}/retry",async(Guid id,int expectedRetryCount,HttpContext c,IPhaseOne s,CancellationToken ct)=>{await s.RetryJob(Actor(c),id,expectedRetryCount,ct);return Results.NoContent();});
        admin.MapGet("/demand",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Demand(Actor(c),false,Page(page,pageSize),ct));
        admin.MapGet("/metrics",(HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Metrics(Actor(c),ct));
        admin.MapGet("/orders",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Orders(Actor(c),Page(page,pageSize),true,ct));
        admin.MapPost("/orders/{id:guid}/retry",async(Guid id,int expectedRetryCount,HttpContext c,IPhaseOne s,CancellationToken ct)=>{await s.RetryPayment(Actor(c),id,expectedRetryCount,ct);return Results.NoContent();});
        admin.MapGet("/refunds",(int? page,int? pageSize,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Refunds(Actor(c),Page(page,pageSize),ct));
        admin.MapPost("/refunds/{id:guid}/retry",async(Guid id,int expectedRetryCount,HttpContext c,IPhaseOne s,CancellationToken ct)=>{await s.RetryRefund(Actor(c),id,expectedRetryCount,ct);return Results.NoContent();});
        admin.MapPost("/fulfillments/{id:guid}/refund",async(Guid id,ReviewRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>{await s.FulfillmentRefund(Actor(c),id,r.Reason??"",ct);return Results.NoContent();});
        MapFulfillment(admin,"admin");
    }
    private static void MapFulfillment(RouteGroupBuilder group,string view)
    {
        group.MapPost("/fulfillments/verify",(SecretRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.VerifyOpaquePass(Actor(c),r,ct)).RequireRateLimiting("pickup");
        group.MapGet("/fulfillments",(int? page,int? pageSize,string? area,Guid? seller,Guid? campaign,FulfillmentState? state,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Fulfillments(Actor(c),view,Page(page,pageSize),area,seller,campaign,state,ct));
        group.MapGet("/fulfillments/{id:guid}",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Fulfillment(Actor(c),id,view,ct));
        group.MapPost("/fulfillments/{id:guid}/status",(Guid id,FulfillmentAction r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.MoveFulfillment(Actor(c),id,r,ct));
        group.MapPost("/fulfillments/{id:guid}/verify",(Guid id,SecretRequest r,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.VerifyPass(Actor(c),id,r,ct)).RequireRateLimiting("pickup");
        group.MapPost("/fulfillments/{id:guid}/redeem",(Guid id,HttpContext c,IPhaseOne s,CancellationToken ct)=>s.Redeem(Actor(c),id,ct)).RequireRateLimiting("pickup");
    }
}
