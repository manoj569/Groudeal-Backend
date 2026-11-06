using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GroupBuy.Application;
using GroupBuy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GroupBuy.IntegrationTests;

public sealed partial class CustomerAuthenticationTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("mobile")]
    public async Task Me_contract_returns_only_own_identity_contacts_and_actual_methods(string channel)
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var client=f.CreateClient();
        var login=await Login(f,client,channel);
        string? email;string? phone;
        await using(var scope=f.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<MarketplaceDb>();var a=await db.Users.SingleAsync(x=>x.Id==login.User.Id);
            email=a.NormalizedEmail;phone=a.PhoneNumber;
            var p=await db.UsersProfile.SingleAsync(x=>x.Id==a.Id);p.Email="untrusted-profile-"+Guid.NewGuid().ToString("N")+"@example.invalid";p.EmailVerified=true;p.PhoneNumber=Destination("mobile");p.PhoneNumberVerified=true;await db.SaveChangesAsync();
        }
        var response=await client.GetAsync("/api/v1/me");var raw=await response.Content.ReadAsStringAsync();var me=await Read<CustomerProfile>(response);
        Assert.Equal(email,me.Email);Assert.Equal(phone,me.Mobile);Assert.Equal(channel=="email",me.EmailVerified);Assert.Equal(channel=="mobile",me.PhoneVerified);
        Assert.Equal(new ConnectedAuthenticationMethods(false,channel=="email",channel=="mobile"),me.ConnectedMethods);
        foreach(var name in new[]{"passwordHash","securityStamp","concurrencyStamp","providerKey","providerSubject","codeHash","credential","tokenVersion","mfaSecret","recoveryCodes"})Assert.DoesNotContain(name,raw,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("untrusted-profile",raw);
    }
    [Fact]
    public async Task Google_only_me_does_not_infer_a_mailbox_or_phone()
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var client=f.CreateClient();
        var login=await Read<CustomerLogin>(await client.PostAsJsonAsync("/api/v1/auth/google",new GoogleCredential("no-email-"+Guid.NewGuid().ToString("N"))));
        client.DefaultRequestHeaders.Authorization=new("Bearer",login.Session.AccessToken);
        var me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.Null(me.Email);Assert.Null(me.Mobile);
        Assert.False(me.EmailVerified);Assert.False(me.PhoneVerified);Assert.Equal(new ConnectedAuthenticationMethods(true,false,false),me.ConnectedMethods);
    }
    [Fact]
    public async Task Gmail_address_does_not_imply_a_Google_connection()
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var client=f.CreateClient();
        var c=await Request(f,client,"email",Guid.NewGuid().ToString("N")+"@gmail.com");
        var login=await Read<CustomerLogin>(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,c.Code)));
        client.DefaultRequestHeaders.Authorization=new("Bearer",login.Session.AccessToken);
        var me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.True(me.EmailVerified);Assert.False(me.ConnectedMethods!.Google);
    }
    [Fact]
    public async Task Me_immediately_reflects_all_links_and_persisted_consent_versions()
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var client=f.CreateClient();
        var login=await Login(f,client,"mobile");
        var email=Destination("email");var challenge=await Request(f,client,"email",email,true);
        await Read<CustomerProfile>(await client.PostAsJsonAsync("/api/v1/me/link/email/verify",new OtpVerification(challenge.Challenge.ChallengeId,challenge.Code)));
        var me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.Equal(email.ToUpperInvariant(),me.Email);Assert.True(me.ConnectedMethods!.Email);Assert.False(me.ConnectedMethods.Google);
        await Read<CustomerProfile>(await client.PostAsJsonAsync("/api/v1/me/link/google",new GoogleCredential(Guid.NewGuid().ToString("N"))));
        me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.Equal(new ConnectedAuthenticationMethods(true,true,true),me.ConnectedMethods);
        Assert.Null(me.Consent!.TermsAcceptedVersion);Assert.True(me.Consent.ConsentRequired);
        await Read<CustomerProfile>(await client.PostAsJsonAsync("/api/v1/me/consent",new ConsentAcceptance("test-terms-v1","test-privacy-v1")));
        me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.Equal("test-terms-v1",me.Consent!.TermsAcceptedVersion);Assert.Equal("test-privacy-v1",me.Consent.PrivacyAcceptedVersion);Assert.False(me.Consent.ConsentRequired);
        f.Services.GetRequiredService<IConfiguration>()["Authentication:Consent:TermsVersion"]="test-terms-v2";
        me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.True(me.Consent!.ConsentRequired);Assert.Equal("test-terms-v1",me.Consent.TermsAcceptedVersion);Assert.Equal("test-terms-v2",me.TermsVersion);
        f.Services.GetRequiredService<IConfiguration>()["Authentication:Consent:TermsVersion"]=null;
        me=await Read<CustomerProfile>(await client.GetAsync("/api/v1/me"));Assert.True(me.Consent!.ConsentRequired);Assert.Equal("test-terms-v1",me.Consent.TermsAcceptedVersion);
        await client.PostAsync("/api/v1/auth/logout",null);Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/me")).StatusCode);
    }
    [Fact]
    public async Task Me_immediately_reflects_linked_mobile_and_conflicts_do_not_disclose_another_account()
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var a=f.CreateClient();using var b=f.CreateClient();await Login(f,a,"email");await Login(f,b,"email");
        var phone=Destination("mobile");var c=await Request(f,a,"mobile",phone,true);
        await Read<CustomerProfile>(await a.PostAsJsonAsync("/api/v1/me/link/mobile/verify",new OtpVerification(c.Challenge.ChallengeId,c.Code)));
        var owner=await Read<CustomerProfile>(await a.GetAsync("/api/v1/me"));Assert.Equal(phone,owner.Mobile);Assert.True(owner.ConnectedMethods!.Mobile);Assert.False(owner.ConnectedMethods.Google);
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().CustomerChallenges.Where(x=>x.Id==c.Challenge.ChallengeId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.CreatedAtUtc,DateTimeOffset.UtcNow.AddMinutes(-2)));
        c=await Request(f,b,"mobile",phone,true);var conflict=await b.PostAsJsonAsync("/api/v1/me/link/mobile/verify",new OtpVerification(c.Challenge.ChallengeId,c.Code));
        Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);var raw=await conflict.Content.ReadAsStringAsync();Assert.DoesNotContain(owner.Id.ToString(),raw);Assert.DoesNotContain(owner.Email!,raw);Assert.DoesNotContain(phone,raw);
        Assert.Equal("identity_conflict",JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString());
        var other=await Read<CustomerProfile>(await b.GetAsync("/api/v1/me"));Assert.Null(other.Mobile);Assert.False(other.ConnectedMethods!.Mobile);
    }
    [Fact]
    public async Task Otp_error_codes_are_context_bound_and_keep_legacy_titles()
    {
        using var f=new CustomerAuthFactory(fixture.Connection);using var client=f.CreateClient();var destination=Destination("email");var c=await Request(f,client,"email",destination);
        async Task<string?> Code(HttpResponseMessage r,string title){var json=JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;Assert.Equal(title,json.GetProperty("title").GetString());return json.GetProperty("code").GetString();}
        Assert.Equal("otp_resend_cooldown",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/request-otp",new OtpRequest(destination)),"challenge_rate_limited"));
        var wrong=c.Code=="000000"?"000001":"000000";
        Assert.Equal("otp_invalid",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,wrong)),"invalid_challenge"));
        // Knowing an unrelated challenge ID does not expose its expiry or remaining attempts.
        Assert.Equal("authentication_invalid",await Code(await client.PostAsJsonAsync("/api/v1/auth/mobile/verify-otp",new OtpVerification(c.Challenge.ChallengeId,c.Code)),"invalid_challenge"));
        await using(var scope=f.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().CustomerChallenges.Where(x=>x.Id==c.Challenge.ChallengeId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ExpiresAtUtc,DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal("otp_expired",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,c.Code)),"invalid_challenge"));
        c=await Request(f,client,"email",Destination("email"));wrong=c.Code=="000000"?"000001":"000000";
        for(var i=0;i<4;i++)Assert.Equal("otp_invalid",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,wrong)),"invalid_challenge"));
        Assert.Equal("otp_attempts_exceeded",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,wrong)),"invalid_challenge"));
        Assert.Equal("otp_attempts_exceeded",await Code(await client.PostAsJsonAsync("/api/v1/auth/email/verify-otp",new OtpVerification(c.Challenge.ChallengeId,c.Code)),"invalid_challenge"));
    }
}
