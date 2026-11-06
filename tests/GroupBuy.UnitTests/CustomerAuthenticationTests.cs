using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace GroupBuy.UnitTests;

public sealed class CustomerAuthenticationTests
{
    [Theory]
    [InlineData("email", " Test@Example.com ", "TEST@EXAMPLE.COM")]
    [InlineData("email", "te\u0301st@example.com", "TÉST@EXAMPLE.COM")]
    [InlineData("email", "tést@example.com", "TÉST@EXAMPLE.COM")]
    [InlineData("mobile", "+91 (98765) 43210", "+919876543210")]
    public void Canonical_destinations(string channel, string input, string expected) => Assert.Equal(expected, CustomerIdentityNormalization.Normalize(channel, input));
    [Theory]
    [InlineData("mobile", "9876543210")]
    [InlineData("mobile", "+01234567890")]
    [InlineData("email", "Name <test@example.com>")]
    [InlineData("email", "invalid")]
    public void Reject_ambiguous_destinations(string channel, string input) => Assert.Throws<BusinessException>(() => CustomerIdentityNormalization.Normalize(channel, input));

    private sealed class Keys(SecurityKey key) : IGoogleSigningKeys
    { public Task<IReadOnlyCollection<SecurityKey>> Get(CancellationToken ct) => Task.FromResult<IReadOnlyCollection<SecurityKey>>([key]); }
    [Theory]
    [InlineData("valid")]
    [InlineData("signature")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("subject")]
    [InlineData("azp")]
    [InlineData("third-party")]
    [InlineData("future-iat")]
    [InlineData("missing-iat")]
    [InlineData("algorithm")]
    public async Task Google_cryptographic_validation_without_network(string fault)
    {
        using var rsa = RSA.Create(2048); using var other = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Authentication:Google:ClientId"] = "test-client" }).Build();
        var validator = new GoogleCredentialValidator(new Keys(key), config, TimeProvider.System);
        var claims = new List<Claim> { new("iat", DateTimeOffset.UtcNow.AddMinutes(fault == "future-iat" ? 10 : -2).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("email", fault == "third-party" ? "user@example.com" : "user@gmail.com"), new("email_verified", "true", ClaimValueTypes.Boolean) };
        if (fault != "subject") claims.Add(new("sub", "immutable-subject"));
        if (fault == "missing-iat") claims.RemoveAll(x => x.Type == "iat");
        if (fault == "azp") claims.Add(new("azp", "untrusted-client"));
        var token = new JwtSecurityToken(fault == "issuer" ? "https://untrusted.invalid" : "https://accounts.google.com",
            fault == "audience" ? "other-client" : "test-client", claims, DateTime.UtcNow.AddMinutes(-3),
            fault == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(fault == "signature" ? new RsaSecurityKey(other) { KeyId = "test-key" } : key, fault == "algorithm" ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.RsaSha256));
        var credential = new JwtSecurityTokenHandler().WriteToken(token);
        if (fault is "valid" or "third-party")
        {
            var result = await validator.Validate(credential, default); Assert.Equal("immutable-subject", result.Subject);
            Assert.Equal(fault == "valid" ? "user@gmail.com" : null, result.Email);
        }
        else Assert.Equal(401, (await Assert.ThrowsAsync<BusinessException>(() => validator.Validate(credential, default))).StatusCode);
    }
}
