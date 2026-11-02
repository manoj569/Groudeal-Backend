namespace GroupBuy.Domain;

public sealed class AdminMfaChallenge : Entity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public int TokenVersion { get; set; }
    public string SecurityStamp { get; set; } = "";
    public bool Enrollment { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public int FailedAttempts { get; set; }
}
public sealed class AcceptedMfaCode
{
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset AcceptedAtUtc { get; set; }
}
