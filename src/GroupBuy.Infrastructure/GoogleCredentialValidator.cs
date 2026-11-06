using System.IdentityModel.Tokens.Jwt;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace GroupBuy.Infrastructure;

public interface IGoogleSigningKeys { Task<IReadOnlyCollection<SecurityKey>> Get(CancellationToken ct); }
public sealed class GoogleSigningKeys(IHttpClientFactory clients, TimeProvider clock) : IGoogleSigningKeys
{
    private readonly SemaphoreSlim gate = new(1);
    private IReadOnlyCollection<SecurityKey> keys = [];
    private DateTimeOffset refreshAt;
    public async Task<IReadOnlyCollection<SecurityKey>> Get(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (refreshAt <= clock.GetUtcNow())
            {
                // Only this fixed HTTPS trust source can supply signing keys.
                var json = await clients.CreateClient("GoogleSigningKeys").GetStringAsync("https://www.googleapis.com/oauth2/v3/certs", ct);
                keys = new JsonWebKeySet(json).GetSigningKeys().ToArray();
                refreshAt = clock.GetUtcNow().AddMinutes(5);
            }
            return keys;
        }
        finally { gate.Release(); }
    }
}
public sealed class GoogleCredentialValidator(IGoogleSigningKeys keys, IConfiguration config, TimeProvider clock) : IGoogleCredentialValidator
{
    public async Task<GoogleIdentity> Validate(string credential, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(credential) || credential.Length > 16384 || string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"])) throw new SecurityTokenException();
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
            var principal = handler.ValidateToken(credential, new TokenValidationParameters {
                ValidateIssuer = true, ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
                ValidateAudience = true, ValidAudience = config["Authentication:Google:ClientId"],
                ValidateIssuerSigningKey = true, IssuerSigningKeys = await keys.Get(ct), RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (nbf, exp, _, _) => exp.HasValue && exp.Value > clock.GetUtcNow().UtcDateTime && (!nbf.HasValue || nbf.Value <= clock.GetUtcNow().UtcDateTime)
            }, out _);
            var sub = principal.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(sub) || sub.Length > 255 || !long.TryParse(principal.FindFirst("iat")?.Value, out var issued) || issued > clock.GetUtcNow().ToUnixTimeSeconds() + 15) throw new SecurityTokenException();
            var azp = principal.FindFirst("azp")?.Value;
            if (azp != null && azp != config["Authentication:Google:ClientId"]) throw new SecurityTokenException();
            var email = principal.FindFirst("email")?.Value;
            // Google does not establish continuing ownership of arbitrary third-party mailboxes.
            var authoritative = principal.FindFirst("email_verified")?.Value == "true" && email != null &&
                (email.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(principal.FindFirst("hd")?.Value));
            return new(sub, authoritative ? email : null, principal.FindFirst("name")?.Value);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or FormatException)
        { throw new BusinessException(401, "invalid_google_credential", "Google credential unavailable or invalid."); }
        catch (HttpRequestException) { throw new BusinessException(503, "google_unavailable", "Google validation is temporarily unavailable."); }
    }
}
