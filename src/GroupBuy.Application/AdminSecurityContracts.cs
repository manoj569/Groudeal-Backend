namespace GroupBuy.Application;

public record MfaChallengeRequest(string Challenge);
public record MfaVerifyRequest(string Challenge, string Code, bool UseRecoveryCode = false);
public record MfaEnrollmentResponse(string SharedKey, string Issuer, string AccountName);
public record MfaTokenResponse(string AccessToken, DateTimeOffset ExpiresAtUtc, IReadOnlyList<string> RecoveryCodes);
public interface IAdminMfa
{
    Task<MfaEnrollmentResponse> Enrollment(MfaChallengeRequest request, CancellationToken ct);
    Task<MfaTokenResponse> Verify(MfaVerifyRequest request, CancellationToken ct);
}
