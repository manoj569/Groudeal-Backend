using System.Net;
using System.Security.Cryptography;
using System.Text;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
namespace GroupBuy.IntegrationTests;
public class PaymentGatewayTests
{
    [Fact]public void Production_authentication_quota_remains_automatic_ten_per_minute()
    {
        var options=new GroupBuy.API.AuthenticationRateLimitSettings().CreateOptions();
        Assert.Equal(10,options.PermitLimit);Assert.Equal(TimeSpan.FromMinutes(1),options.Window);Assert.Equal(0,options.QueueLimit);Assert.True(options.AutoReplenishment);
    }
    private static readonly string TestSecret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static IConfiguration Configuration(Dictionary<string,string?>? additional=null)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Payments:Razorpay:Mode"]="Test",["Payments:Razorpay:KeyId"]="rzp_test_synthetic",["Payments:Razorpay:KeySecret"]=TestSecret,["Payments:Razorpay:WebhookSecret"]=TestSecret}.Concat(additional??new()).GroupBy(p=>p.Key).Select(g=>g.Last())).Build();
    private sealed class Factory(Func<HttpRequestMessage,HttpResponseMessage> send):IHttpClientFactory
    {
        private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(send(request));}
        public HttpClient CreateClient(string name)=>new(new Handler(send));
    }
    private static HttpResponseMessage Json(string value)=>new(HttpStatusCode.OK){Content=new StringContent(value,Encoding.UTF8,"application/json")};
    private static string Signature(string text)=>Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestSecret),Encoding.UTF8.GetBytes(text)));
    [Fact]public async Task Disabled_provider_fails_closed()=>Assert.Equal(503,(await Assert.ThrowsAsync<BusinessException>(()=>new DisabledPaymentGateway().CreateOrder(Guid.NewGuid(),100,default))).StatusCode);
    [Fact]public async Task Callback_signature_is_required_before_any_provider_request()
    {
        var calls=0;var gateway=new RazorpayPaymentGateway(new Factory(_=>{calls++;return Json("{}");}),Configuration());await Assert.ThrowsAsync<BusinessException>(()=>gateway.Confirm("order_synthetic","pay_synthetic",new string('0',64),default));Assert.Equal(0,calls);
    }
    [Fact]public async Task Order_receipt_recovery_does_not_repeat_creation()
    {
        var id=Guid.NewGuid();var calls=0;var gateway=new RazorpayPaymentGateway(new Factory(r=>{calls++;Assert.Equal(HttpMethod.Get,r.Method);Assert.Equal("https",r.RequestUri!.Scheme);Assert.Equal("api.razorpay.com",r.RequestUri.Host);return Json($$"""{"items":[{"receipt":"{{id:N}}","id":"order_synthetic","amount":5000,"currency":"INR","status":"created"}]}""");}),Configuration());
        var order=await gateway.CreateOrder(id,5000,default);Assert.Equal("order_synthetic",order.Id);Assert.Equal(1,calls);
    }
    [Fact]public async Task Captured_payment_is_authoritatively_fetched()
    {
        var gateway=new RazorpayPaymentGateway(new Factory(r=>{Assert.Equal("/v1/payments/pay_synthetic",r.RequestUri!.AbsolutePath);return Json("""{"id":"pay_synthetic","order_id":"order_synthetic","amount":5000,"currency":"INR","status":"captured"}""");}),Configuration());
        var payment=await gateway.Confirm("order_synthetic","pay_synthetic",Signature("order_synthetic|pay_synthetic"),default);Assert.Equal("captured",payment.Status);Assert.Equal(5000,payment.AmountMinor);
    }
    [Theory][InlineData(false,"authorized",0)][InlineData(true,"captured",1)]public async Task Reconciliation_captures_authorizations_only_when_the_business_hold_is_valid(bool capture,string expected,int posts)
    {
        var captures=0;var gateway=new RazorpayPaymentGateway(new Factory(r=>{
            if(r.Method==HttpMethod.Post){captures++;return Json("""{"id":"pay_synthetic","order_id":"order_synthetic","amount":5000,"currency":"INR","status":"captured"}""");}
            return r.RequestUri!.AbsolutePath.EndsWith("/payments",StringComparison.Ordinal)?Json("""{"items":[{"id":"pay_synthetic","order_id":"order_synthetic","amount":5000,"currency":"INR","status":"authorized"}]}"""):Json("""{"id":"order_synthetic","amount":5000,"currency":"INR"}""");
        }),Configuration());var payment=await gateway.Reconcile("order_synthetic",default,capture);Assert.Equal(expected,payment!.Status);Assert.Equal(posts,captures);
    }
    [Fact]public void Webhook_requires_signature_and_preserves_event_identity()
    {
        const string raw="""{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_synthetic","order_id":"order_synthetic","amount":5000,"currency":"INR","status":"captured"}}}}""";
        var gateway=new RazorpayPaymentGateway(new Factory(_=>throw new InvalidOperationException()),Configuration());var bytes=Encoding.UTF8.GetBytes(raw);Assert.Throws<BusinessException>(()=>gateway.VerifyWebhook(bytes,new string('0',64),"evt_synthetic"));var e=gateway.VerifyWebhook(bytes,Signature(raw),"evt_synthetic");Assert.Equal("evt_synthetic",e.EventId);Assert.Equal("order_synthetic",e.OrderId);
    }
    [Fact]public async Task Refund_creation_uses_stable_operation_key_and_minor_amount()
    {
        var refund=Guid.NewGuid();var calls=0;var gateway=new RazorpayPaymentGateway(new Factory(r=>{calls++;if(r.Method==HttpMethod.Get)return Json("""{"items":[]}""");Assert.Equal(refund.ToString("N"),r.Headers.GetValues("X-Refund-Idempotency").Single());Assert.Equal("/v1/payments/pay_synthetic/refund",r.RequestUri!.AbsolutePath);return Json("""{"id":"rfnd_synthetic","amount":2000,"status":"processed"}""");}),Configuration());
        var result=await gateway.Refund("pay_synthetic",refund,2000,default);Assert.Equal("processed",result.Status);Assert.Equal(2,calls);
    }
    [Fact]public void Missing_credentials_and_unapproved_live_mode_are_rejected()
    {
        var factory=new Factory(_=>throw new InvalidOperationException());Assert.Throws<InvalidOperationException>(()=>new RazorpayPaymentGateway(factory,new ConfigurationBuilder().Build()));Assert.Throws<InvalidOperationException>(()=>new RazorpayPaymentGateway(factory,Configuration(new(){["Payments:Razorpay:Mode"]="Live",["Payments:Razorpay:KeyId"]="rzp_live_synthetic"})));
    }
    private sealed class Environment(string name):IHostEnvironment
    {public string EnvironmentName{get;set;}=name;public string ApplicationName{get;set;}="Synthetic";public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();}
    private static Dictionary<string,string?> ApprovedPilot()=>new(){["Pilot:Enabled"]="true",["Pilot:Approved"]="true",["Pilot:ServiceAreas:0:Id"]="approved-area",["Pilot:ServiceAreas:0:Name"]="Configuration validation fixture",["Pilot:ServiceAreas:0:PostalCodes:0"]="110001",["Pilot:DeliveryFeeMinor"]="3000",["Pilot:DeliveryFeeApproved"]="true",["Payments:Enabled"]="true",["Payments:Provider"]="Razorpay"};
    [Theory]
    [InlineData("Payments:Enabled","false")]
    [InlineData("Payments:Provider","Sandbox")]
    [InlineData("Payments:Razorpay:Mode","Live")]
    [InlineData("Payments:Razorpay:KeySecret","")]
    [InlineData("Payments:Razorpay:WebhookSecret","")]
    [InlineData("Pilot:Approved","false")]
    [InlineData("Pilot:DeliveryFeeApproved","false")]
    [InlineData("Pilot:DeliveryFeeMinor",null)]
    [InlineData("Pilot:DeliveryFeeMinor","-1")]
    [InlineData("Pilot:ServiceAreas:0:PostalCodes:0","TEST01")]
    public void Controlled_pilot_rejects_incomplete_activation(string key,string? value)
    {
        var values=ApprovedPilot();values[key]=value;
        Assert.Throws<InvalidOperationException>(()=>PilotConfiguration.Validate(Configuration(values),new Environment("Staging")));
    }
    [Fact]public void Approved_test_mode_configuration_passes_without_network_access()=>PilotConfiguration.Validate(Configuration(ApprovedPilot()),new Environment("Staging"));
    [Fact]public void Ordinary_disabled_development_is_allowed()=>PilotConfiguration.Validate(new ConfigurationBuilder().Build(),new Environment("Development"));
    [Fact]public async Task Refund_recovery_searches_later_pages_without_posting()
    {
        var id=Guid.NewGuid();var calls=0;
        var gateway=new RazorpayPaymentGateway(new Factory(r=>{
            Assert.Equal(HttpMethod.Get,r.Method);calls++;
            if(calls==1)return Json("{\"items\":["+string.Join(",",Enumerable.Repeat("{\"receipt\":\"other\"}",100))+"]}");
            Assert.Contains("skip=100",r.RequestUri!.Query);
            return Json($$"""{"items":[{"receipt":"{{id:N}}","id":"rfnd_recovered","amount":2000,"status":"processed"}]}""");
        }),Configuration());
        Assert.Equal("rfnd_recovered",(await gateway.Refund("pay_synthetic",id,2000,default)).Id);Assert.Equal(2,calls);
    }
    [Fact]public void Production_rejects_sandbox_and_missing_pilot_activation()
    {
        Assert.Throws<InvalidOperationException>(()=>PilotConfiguration.Validate(Configuration(new(){["Payments:Provider"]="Sandbox"}),new Environment("Production")));
        Assert.Throws<InvalidOperationException>(()=>PilotConfiguration.Validate(Configuration(new(){["Pilot:Enabled"]="true"}),new Environment("Production")));
    }
}
