namespace GroupBuy.Domain;

public enum DemandType { Product, Service }
public enum DemandStatus { BuildingInterest, Closed, Cancelled }

public sealed class DiscoveryDemand : TrackedEntity
{
    public string Title { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public string ServiceArea { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public DemandType DemandType { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? CreatedBy { get; set; }
    public bool LegacyIdentity { get; set; } = true;
    public bool IsCancelled { get; set; }
    public string? ClosureReason { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public DemandStatus Status => IsOpen ? DemandStatus.BuildingInterest : IsCancelled ? DemandStatus.Cancelled : DemandStatus.Closed;
    public bool IsOpen { get; set; } = true;
}
