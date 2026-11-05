using System.Threading.RateLimiting;

namespace GroupBuy.API;

// Registered through DI so quota tests can hold a window open independently of
// machine speed. Production always uses automatic one-minute replenishment.
public class AuthenticationRateLimitSettings
{
    public FixedWindowRateLimiterOptions CreateOptions() => new()
    {
        PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        AutoReplenishment = true
    };
    public virtual RateLimitPartition<string> Partition(string key) => RateLimitPartition.GetFixedWindowLimiter(key, _ => CreateOptions());
}
