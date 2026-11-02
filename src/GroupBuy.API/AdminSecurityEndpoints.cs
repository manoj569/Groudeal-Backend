using GroupBuy.Application;
namespace GroupBuy.API;
public static class AdminSecurityEndpoints
{
    public static void MapAdminSecurity(this WebApplication app)
    {
        var mfa = app.MapGroup("/api/v1/auth/mfa").RequireRateLimiting("mfa").WithTags("Admin MFA");
        mfa.MapPost("/enrollment", (MfaChallengeRequest r, IAdminMfa service, CancellationToken ct) => service.Enrollment(r, ct));
        mfa.MapPost("/verify", (MfaVerifyRequest r, IAdminMfa service, CancellationToken ct) => service.Verify(r, ct));
    }
}
