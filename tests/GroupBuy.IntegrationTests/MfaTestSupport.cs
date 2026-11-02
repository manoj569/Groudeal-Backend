using System.Net.Http.Json;
using GroupBuy.Application;
using GroupBuy.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace GroupBuy.IntegrationTests;
public partial class BackendTests
{
    private static async Task<MfaTokenResponse> CompleteMfa(HttpClient client, UserResponse user, TokenResponse token)
    {
        var f = ApiFactory.ClientFactories.GetValue(client, _ => throw new InvalidOperationException("Test client factory missing."));
        string key;
        if (token.EnrollmentRequired) key = (await Read<MfaEnrollmentResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/enrollment", new MfaChallengeRequest(token.MfaChallenge!)))).SharedKey;
        else
        {
            await using var scope = f.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(user.Id.ToString()))!))!;
        }
        var code = await UnusedTotp(f, user.Id, key);
        return await Read<MfaTokenResponse>(await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(token.MfaChallenge!, code)));
    }
    private static async Task<string> UnusedTotp(ApiFactory f, Guid userId, string key)
    {
        var totp = new Totp(Base32Encoding.ToBytes(key));
        await using var scope = f.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        foreach (var offset in new[] { 0, 1, -1, 2, -2 })
        {
            var code = totp.ComputeTotp(DateTime.UtcNow.AddSeconds(offset * 30)); var hash = SecurityTokens.Hash($"{userId:N}:{code}");
            if (!await db.AcceptedMfaCodes.AnyAsync(c => c.UserId == userId && c.CodeHash == hash && c.AcceptedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-10))) return code;
        }
        throw new InvalidOperationException("Test exhausted the authenticator verification window.");
    }
}
