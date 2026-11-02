namespace GroupBuy.Application;

public record WorkerSnapshot(DateTimeOffset? LastStartedAtUtc, DateTimeOffset? LastCompletedAtUtc, bool Running, string? LastBatchErrorType, DateTimeOffset? LastProgressAtUtc = null);

// Process-local liveness complements persisted per-campaign retries. Empty after restart is intentional.
public sealed class WorkerHealthState
{
    private readonly object gate = new();
    private WorkerSnapshot snapshot = new(null, null, false, null);
    public void Started(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastStartedAtUtc = now, LastProgressAtUtc = now, Running = true }; }
    public void Completed(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastCompletedAtUtc = now, Running = false, LastBatchErrorType = null }; }
    public void Failed(string type) { lock (gate) snapshot = snapshot with { Running = false, LastBatchErrorType = type }; }
    public void Progress(DateTimeOffset now) { lock (gate) snapshot = snapshot with { LastProgressAtUtc = now }; }
    public void Stopped() { lock (gate) snapshot = snapshot with { Running = false }; }
    public WorkerSnapshot Read() { lock (gate) return snapshot; }
}
