using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using GroupBuy.Domain;

namespace GroupBuy.Application;

public record OtpRequest(string Destination);
public record OtpVerification(Guid ChallengeId, string Code);
public record GoogleCredential(string Credential);
public record ProfileCompletion(string DisplayName);
public record ConsentAcceptance(string TermsVersion, string PrivacyVersion);
public record CustomerProfile(Guid Id, string DisplayName, bool ProfileCompleted, bool EmailVerified, bool PhoneVerified, bool ConsentRequired, string? TermsVersion, string? PrivacyVersion);
public record CustomerLogin(TokenResponse Session, bool Authenticated, CustomerProfile User);
public record ChallengeResponse(Guid ChallengeId, DateTimeOffset ExpiresAtUtc);
public record GoogleIdentity(string Subject, string? Email, string? Name);
public interface IGoogleCredentialValidator { Task<GoogleIdentity> Validate(string credential, CancellationToken ct); }
public interface IOtpDeliveryProvider
{
    bool Supports(string channel);
    Task Deliver(string channel, string destination, string code, CancellationToken ct);
}
public static class CustomerIdentityNormalization
{
    public static string Normalize(string channel, string? value)
    {
        try { value = (value?.Trim() ?? "").Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new BusinessException(400, "invalid_destination", "Supply a valid email or international phone number."); }
        if (channel == "email" && value.Length <= 254 && MailAddress.TryCreate(value, out var address) && address.Address == value && !value.Any(char.IsWhiteSpace))
            return value.ToUpperInvariant();
        if (channel == "mobile")
        {
            value = Regex.Replace(value, "[ ()-]", "");
            if (Regex.IsMatch(value, @"^\+[1-9][0-9]{7,14}$")) return value;
        }
        throw new BusinessException(400, "invalid_destination", "Supply a valid email or international phone number.");
    }
}
