using System.Security.Claims;
using GroupBuy.Application;
using GroupBuy.Infrastructure;

namespace GroupBuy.API;

public static class CustomerAuthenticationEndpoints
{
    private static Guid Id(HttpContext c) => Guid.Parse(c.User.FindFirstValue("sub")!);
    public static void MapCustomerAuthentication(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Customer authentication").RequireRateLimiting("customer-login");
        auth.MapPost("/google", (GoogleCredential r, CustomerAuthenticationService s, CancellationToken ct) => s.Google(r, null, ct));
        var me = app.MapGroup("/api/v1/me").WithTags("Customer profile").RequireAuthorization("Customer").RequireRateLimiting("customer-auth");
        me.MapGet("/", (HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Me(Id(c), ct));
        me.MapPatch("/profile", (ProfileCompletion r, HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Profile(Id(c), r, ct));
        me.MapPost("/consent", (ConsentAcceptance r, HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Consent(Id(c), r, ct));
        me.MapPost("/link/google", (GoogleCredential r, HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Google(r, Id(c), ct));
        foreach (var channel in new[] { "email", "mobile" })
        {
            var captured = channel;
            auth.MapPost($"/{channel}/request-otp", (OtpRequest r, CustomerAuthenticationService s, CancellationToken ct) => s.Request(captured, r, null, ct));
            auth.MapPost($"/{channel}/verify-otp", (OtpVerification r, CustomerAuthenticationService s, CancellationToken ct) => s.Verify(captured, r, null, ct));
            me.MapPost($"/link/{channel}/request", (OtpRequest r, HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Request(captured, r, Id(c), ct));
            me.MapPost($"/link/{channel}/verify", (OtpVerification r, HttpContext c, CustomerAuthenticationService s, CancellationToken ct) => s.Verify(captured, r, Id(c), ct));
        }
    }
    public static void ValidateCustomerAuthentication(this WebApplication app)
    {
        var config = app.Configuration;
        foreach (var channel in new[] { "email", "mobile" })
            if (config.GetValue($"Authentication:{(channel == "email" ? "EmailOtp" : "MobileOtp")}:Enabled", false) && !app.Services.GetRequiredService<IOtpDeliveryProvider>().Supports(channel))
                throw new InvalidOperationException("Enabled customer OTP requires a configured delivery provider.");
        if (config.GetValue("Authentication:Google:Enabled", false) && string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"]))
            throw new InvalidOperationException("Enabled Google authentication requires a client ID.");
        foreach (var key in new[] { "TermsVersion", "PrivacyVersion" })
            if (config[$"Authentication:Consent:{key}"] is string version && (string.IsNullOrWhiteSpace(version) || version.Length > 100))
                throw new InvalidOperationException("Consent versions must contain 1–100 characters.");
        if (!app.Environment.IsDevelopment() && (config.GetValue("Pilot:Enabled", false) || config.GetValue("Authentication:Google:Enabled", false) ||
            config.GetValue("Authentication:EmailOtp:Enabled", false) || config.GetValue("Authentication:MobileOtp:Enabled", false)) &&
            (string.IsNullOrWhiteSpace(config["Authentication:Consent:TermsVersion"]) || string.IsNullOrWhiteSpace(config["Authentication:Consent:PrivacyVersion"])))
            throw new InvalidOperationException("Production customer authentication/pilot activation requires approved Terms and Privacy version identifiers.");
    }
}
