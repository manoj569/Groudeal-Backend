using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupBuy.Infrastructure;

public static class PilotConfiguration
{
    public static ServiceAreaOption[] Areas(IConfiguration c) => c.GetSection("Pilot:ServiceAreas").Get<ServiceAreaOption[]>() ?? [];
    public static void Validate(IConfiguration c,IHostEnvironment env)
    {
        if(env.IsProduction() && c["Payments:Provider"]=="Sandbox") throw new InvalidOperationException("Sandbox payments are forbidden in Production.");
        if(!c.GetValue("Pilot:Enabled",false)) return;
        var isolated=env.IsDevelopment() || env.IsEnvironment("Testing");
        if(!isolated && (!c.GetValue("Payments:Enabled",false) || c["Payments:Provider"]!="Razorpay" || c["Payments:Razorpay:Mode"]!="Test")) throw new InvalidOperationException("Controlled pilot requires enabled Razorpay Test Mode payments.");
        if(c.GetValue("Payments:Enabled",false))
        {
            if(c["Payments:Provider"]=="Razorpay") ValidateRazorpay(c);
            else if(c["Payments:Provider"]!="Sandbox" || !isolated || !c.GetValue("Payments:SandboxEnabled",false) || c["Payments:SandboxWebhookSecret"] is not {Length:>=32}) throw new InvalidOperationException("Configure an explicit isolated payment provider.");
        }
        var areas=Areas(c);
        if(areas.Length is <1 or >10 || areas.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=areas.Length || areas.Any(a=>!Regex.IsMatch(a.Id,"^[a-z0-9-]{1,80}$") || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length>100 || a.PostalCodes.Length is <1 or >30 || a.PostalCodes.Any(p=>p.Length>12 || string.IsNullOrWhiteSpace(p)))) throw new InvalidOperationException("Configure explicit pilot service areas.");
        var pilotActivation=!env.IsDevelopment() && !env.IsEnvironment("Testing");
        if(pilotActivation && (!c.GetValue("Pilot:Approved",false) || areas.Any(a=>a.PostalCodes.Any(p=>!Regex.IsMatch(p,"^[1-9][0-9]{5}$"))))) throw new InvalidOperationException("Pilot activation requires approved real service areas.");
        var fee=c.GetValue<long?>("Pilot:DeliveryFeeMinor");
        if(fee is <0 or >100000 || (pilotActivation && (fee is null || !c.GetValue("Pilot:DeliveryFeeApproved",false)))) throw new InvalidOperationException("Configure an explicit approved delivery fee.");
    }
    public static void ValidateRazorpay(IConfiguration c)
    {
        var mode=c["Payments:Razorpay:Mode"];
        if(mode is not ("Test" or "Live") || c["Payments:Razorpay:KeyId"] is not string key || !Regex.IsMatch(key,mode=="Live"?"^rzp_live_[A-Za-z0-9]+$":"^rzp_test_[A-Za-z0-9]+$") || c["Payments:Razorpay:KeySecret"] is not {Length:>=16} || c["Payments:Razorpay:WebhookSecret"] is not {Length:>=16} || (mode=="Live" && !c.GetValue("Payments:Razorpay:LiveApproved",false))) throw new InvalidOperationException("Razorpay mode, credentials and activation approval are required.");
    }
    public static IPaymentGateway Gateway(IServiceProvider s)
    {
        var c=s.GetRequiredService<IConfiguration>();var env=s.GetRequiredService<IHostEnvironment>();Validate(c,env);
        if(!c.GetValue("Pilot:Enabled",false) || !c.GetValue("Payments:Enabled",false)) return new DisabledPaymentGateway();
        return c["Payments:Provider"] switch {
            "Sandbox" when (env.IsDevelopment() || env.IsEnvironment("Testing")) && c.GetValue("Payments:SandboxEnabled",false)=>new SandboxPaymentGateway(s.GetRequiredService<IServiceScopeFactory>(),c),
            "Razorpay"=>new RazorpayPaymentGateway(s.GetRequiredService<IHttpClientFactory>(),c),
            _=>throw new InvalidOperationException("Explicit payment provider configuration is required.")
        };
    }
    public static bool ValidSignature(byte[] raw,string supplied,string secret)
    {
        if(supplied is null || supplied.Length!=64 || !supplied.All(Uri.IsHexDigit))return false;
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),raw),Convert.FromHexString(supplied));
    }
}
public sealed class DisabledPaymentGateway : IPaymentGateway
{
    public long MinimumRefundMinor=>1;
    public string Name=>"Disabled";public bool Sandbox=>false;public bool Available=>false;public string? CheckoutKeyId=>null;
    private static BusinessException Error()=>new(503,"payments_unavailable","Payment processing is not activated.");
    public Task<ProviderOrder> CreateOrder(Guid id,long amount,CancellationToken ct)=>throw Error();
    public Task<ProviderPayment?> Reconcile(string id,CancellationToken ct,bool captureAuthorized=false)=>throw Error();
    public Task<ProviderPayment> Confirm(string order,string payment,string signature,CancellationToken ct)=>throw Error();
    public VerifiedPaymentEvent VerifyWebhook(byte[] raw,string signature,string id)=>throw Error();
    public Task<ProviderRefund> Refund(string payment,Guid id,long amount,CancellationToken ct)=>throw Error();
}
public sealed class SandboxPaymentGateway(IServiceScopeFactory scopes,IConfiguration config) : IPaymentGateway
{
    public long MinimumRefundMinor=>1;
    public string Name=>"Sandbox";public bool Sandbox=>true;public bool Available=>true;public string? CheckoutKeyId=>null;
    private string Secret => config["Payments:SandboxWebhookSecret"] is {Length:>=32} key ? key : throw new InvalidOperationException("Configure the isolated sandbox webhook secret.");
    private static async Task Lock(MarketplaceDb db,string key,CancellationToken ct)=>_ = await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",ct);
    public async Task<ProviderOrder> CreateOrder(Guid id,long amount,CancellationToken ct)
    {
        _=Secret;await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,id.ToString(),ct);
        var r=await db.SandboxReceipts.SingleOrDefaultAsync(x=>x.LocalOrderId==id,ct);
        if(r is null){r=new(){LocalOrderId=id,ProviderOrderId="sandbox_"+id.ToString("N"),AmountMinor=amount};db.SandboxReceipts.Add(r);await db.SaveChangesAsync(ct);}else if(r.AmountMinor!=amount)throw new BusinessException(409,"provider_payload_conflict","Order amount changed.");
        await tx.CommitAsync(ct);return new(r.ProviderOrderId,r.State,r.AmountMinor,"INR");
    }
    public async Task<ProviderPayment?> Reconcile(string id,CancellationToken ct,bool captureAuthorized=false)
    {
        await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();var r=await db.SandboxReceipts.AsNoTracking().SingleOrDefaultAsync(x=>x.ProviderOrderId==id,ct);
        return r is null?null:new("pay_"+r.LocalOrderId.ToString("N"),id,r.State,r.AmountMinor,"INR");
    }
    public Task<ProviderPayment> Confirm(string order,string payment,string signature,CancellationToken ct)=>throw new BusinessException(400,"sandbox_checkout","Use the explicitly labeled sandbox checkout.");
    public VerifiedPaymentEvent VerifyWebhook(byte[] raw,string signature,string eventId)
    {
        if(!PilotConfiguration.ValidSignature(raw,signature,Secret))throw new BusinessException(401,"invalid_signature","Invalid webhook signature.");
        try{var e=JsonSerializer.Deserialize<VerifiedPaymentEvent>(raw,new JsonSerializerOptions(JsonSerializerDefaults.Web));if(e is null || e.EventId!=eventId || eventId.Length is <1 or >120 || e.OrderId is not {Length:>=1 and <=100})throw new JsonException();return e;}catch(JsonException){throw new BusinessException(400,"invalid_webhook","Malformed provider event.");}
    }
    public async Task<(byte[] Raw,string Signature,string EventId)> Simulate(string id,string state,CancellationToken ct)
    {
        if(state is not ("authorized" or "captured" or "failed"))throw new BusinessException(400,"invalid_sandbox_state","Invalid simulated state.");
        await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();await using var tx=await db.Database.BeginTransactionAsync(ct);
        var initial=await db.SandboxReceipts.AsNoTracking().SingleAsync(x=>x.ProviderOrderId==id,ct);await Lock(db,initial.LocalOrderId.ToString(),ct);
        var r=await db.SandboxReceipts.SingleAsync(x=>x.Id==initial.Id,ct);if(r.State!="captured")r.State=state;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        var eid=Guid.NewGuid().ToString("N");var raw=JsonSerializer.SerializeToUtf8Bytes(new VerifiedPaymentEvent(eid,id,"pay_"+r.LocalOrderId.ToString("N"),r.State,r.AmountMinor,"INR"),new JsonSerializerOptions(JsonSerializerDefaults.Web));return(raw,Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),raw)),eid);
    }
    public async Task<ProviderRefund> Refund(string paymentId,Guid id,long amount,CancellationToken ct)
    {
        var local=Guid.ParseExact(paymentId.Replace("pay_","",StringComparison.Ordinal),"N");await using var scope=scopes.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(db,local.ToString(),ct);
        var previous=await db.SandboxRefundReceipts.SingleOrDefaultAsync(x=>x.RefundId==id,ct);var r=await db.SandboxReceipts.SingleAsync(x=>x.LocalOrderId==local,ct);
        if(previous is not null){if(previous.AmountMinor!=amount)throw new BusinessException(409,"refund_payload_conflict","Refund amount changed.");}
        else {if(r.State!="captured" || amount<=0 || r.RefundedMinor+amount>r.AmountMinor)throw new BusinessException(409,"invalid_refund","Refund exceeds captured funds.");db.SandboxRefundReceipts.Add(new(){RefundId=id,LocalOrderId=local,AmountMinor=amount});r.RefundedMinor+=amount;await db.SaveChangesAsync(ct);}
        await tx.CommitAsync(ct);return new("refund_"+id.ToString("N"),"processed",amount);
    }
}
public sealed class RazorpayPaymentGateway : IPaymentGateway
{
    // Razorpay's documented normal-refund floor is INR 1.00. Reject an
    // unrefundable checkout before taking money, rather than crediting a wallet.
    public long MinimumRefundMinor=>100;
    private readonly IHttpClientFactory clients;private readonly string keyId,keySecret,webhookSecret;private readonly string? account;
    public RazorpayPaymentGateway(IHttpClientFactory clients,IConfiguration config)
    {
        this.clients=clients;keyId=config["Payments:Razorpay:KeyId"]??"";keySecret=config["Payments:Razorpay:KeySecret"]??"";webhookSecret=config["Payments:Razorpay:WebhookSecret"]??"";account=config["Payments:Razorpay:AccountId"];
        PilotConfiguration.ValidateRazorpay(config);
    }
    public string Name=>"Razorpay";public bool Sandbox=>false;public bool Available=>true;public string? CheckoutKeyId=>keyId;
    private async Task<JsonElement> Send(HttpMethod method,string path,object? body,CancellationToken ct,string? refundKey=null)
    {
        using var client=clients.CreateClient("Razorpay");client.Timeout=TimeSpan.FromSeconds(15);client.MaxResponseContentBufferSize=1024*1024;
        using var request=new HttpRequestMessage(method,new Uri("https://api.razorpay.com/v1/"+path));request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(keyId+":"+keySecret)));
        if(refundKey is not null)request.Headers.Add("X-Refund-Idempotency",refundKey);if(body is not null)request.Content=JsonContent.Create(body);
        using var response=await client.SendAsync(request,ct);if(!response.IsSuccessStatusCode)throw new BusinessException(503,"provider_unavailable","Payment provider requires reconciliation; retry using the same operation.");
        using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));return json.RootElement.Clone();
    }
    private static string Id(string value){if(value.Length is <1 or >100 || !value.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))throw new BusinessException(400,"invalid_provider_id","Invalid provider identifier.");return value;}
    private static ProviderPayment Payment(JsonElement p)=>new(p.GetProperty("id").GetString()!,p.GetProperty("order_id").GetString()!,p.GetProperty("status").GetString()!,p.GetProperty("amount").GetInt64(),p.GetProperty("currency").GetString()!);
    public async Task<ProviderOrder> CreateOrder(Guid id,long amount,CancellationToken ct)
    {
        var receipt=id.ToString("N");var found=await Send(HttpMethod.Get,"orders?receipt="+receipt,null,ct);var matches=found.GetProperty("items").EnumerateArray().Where(x=>x.GetProperty("receipt").GetString()==receipt).ToArray();if(matches.Length>1)throw new BusinessException(409,"provider_order_ambiguous","Reconcile duplicate provider orders before proceeding.");
        var order=matches.Length==1?matches[0]:await Send(HttpMethod.Post,"orders",new{amount,currency="INR",receipt,partial_payment=false,notes=new{groudeal_order=id.ToString("N")}},ct);
        if(order.GetProperty("amount").GetInt64()!=amount || order.GetProperty("currency").GetString()!="INR")throw new BusinessException(409,"provider_amount_mismatch","Provider order does not match the authorized amount.");
        return new(order.GetProperty("id").GetString()!,order.GetProperty("status").GetString()!,amount,"INR");
    }
    public async Task<ProviderPayment?> Reconcile(string orderId,CancellationToken ct,bool captureAuthorized=false)
    {
        var order=await Send(HttpMethod.Get,"orders/"+Id(orderId),null,ct);var all=await Send(HttpMethod.Get,"orders/"+Id(orderId)+"/payments",null,ct);
        var payments=all.GetProperty("items").EnumerateArray().Select(Payment).ToList();var p=payments.FirstOrDefault(x=>x.Status=="captured")??payments.FirstOrDefault(x=>x.Status=="authorized")??payments.FirstOrDefault();
        if(p is not null && (p.OrderId!=orderId || p.AmountMinor!=order.GetProperty("amount").GetInt64() || p.Currency!="INR"))throw new BusinessException(409,"provider_amount_mismatch","Provider payment does not match its order.");
        if(captureAuthorized && p?.Status=="authorized") { var captured=await Send(HttpMethod.Post,"payments/"+Id(p.Id)+"/capture",new{amount=p.AmountMinor,currency="INR"},ct);p=Payment(captured); }
        return p;
    }
    public async Task<ProviderPayment> Confirm(string orderId,string paymentId,string signature,CancellationToken ct)
    {
        if(!PilotConfiguration.ValidSignature(Encoding.UTF8.GetBytes(orderId+"|"+paymentId),signature,keySecret))throw new BusinessException(401,"invalid_signature","Invalid payment confirmation.");
        var p=Payment(await Send(HttpMethod.Get,"payments/"+Id(paymentId),null,ct));if(p.OrderId!=orderId)throw new BusinessException(409,"wrong_order","Payment does not belong to this order.");
        if(p.Status=="authorized")p=Payment(await Send(HttpMethod.Post,"payments/"+Id(paymentId)+"/capture",new{amount=p.AmountMinor,currency=p.Currency},ct));return p;
    }
    public VerifiedPaymentEvent VerifyWebhook(byte[] raw,string signature,string eventId)
    {
        if(!PilotConfiguration.ValidSignature(raw,signature,webhookSecret))throw new BusinessException(401,"invalid_signature","Invalid webhook signature.");
        try {
            if(eventId.Length is <1 or >120)throw new JsonException();using var d=JsonDocument.Parse(raw);var e=d.RootElement;if(account is not null && e.GetProperty("account_id").GetString()!=account)throw new JsonException();
            var kind=e.GetProperty("event").GetString();if(kind is not ("payment.authorized" or "payment.captured" or "payment.failed" or "order.paid"))return new(eventId,"ignored","","ignored",0,"INR");
            var p=Payment(e.GetProperty("payload").GetProperty("payment").GetProperty("entity"));return new(eventId,p.OrderId,p.Id,p.Status,p.AmountMinor,p.Currency);
        }catch(JsonException){throw new BusinessException(400,"invalid_webhook","Malformed payment webhook.");}catch(KeyNotFoundException){throw new BusinessException(400,"invalid_webhook","Malformed payment webhook.");}
    }
    public async Task<ProviderRefund> Refund(string paymentId,Guid refundId,long amount,CancellationToken ct)
    {
        if(amount<MinimumRefundMinor)throw new BusinessException(400,"unsupported_refund_amount","Provider refund minimum is not met.");
        var receipt=refundId.ToString("N");JsonElement existing=default;
        // Do not mistake a paginated absence for a new operation. Bound remote work
        // and fail closed if the complete history cannot be inspected.
        for(var page=0;page<10;page++)
        {
            var previous=await Send(HttpMethod.Get,"payments/"+Id(paymentId)+"/refunds?count=100&skip="+(page*100),null,ct);
            var items=previous.GetProperty("items").EnumerateArray().ToArray();
            existing=items.FirstOrDefault(x=>x.TryGetProperty("receipt",out var r)&&r.GetString()==receipt);
            if(existing.ValueKind!=JsonValueKind.Undefined || items.Length<100)break;
            if(page==9)throw new BusinessException(503,"refund_history_limit","Refund history requires operator reconciliation.");
        }
        var refund=existing.ValueKind!=JsonValueKind.Undefined?existing:await Send(HttpMethod.Post,"payments/"+Id(paymentId)+"/refund",new{amount,speed="normal",receipt,notes=new{groudeal_refund=receipt}},ct,receipt);
        if(refund.GetProperty("amount").GetInt64()!=amount)throw new BusinessException(409,"refund_amount_mismatch","Provider refund amount mismatch.");
        return new(refund.GetProperty("id").GetString()!,refund.GetProperty("status").GetString()!,amount);
    }
}
