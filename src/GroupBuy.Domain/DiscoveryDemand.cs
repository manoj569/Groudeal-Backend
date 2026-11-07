namespace GroupBuy.Domain;

public sealed class DiscoveryDemand : TrackedEntity
{
    public string Title { get; set; } = "";
    public string NormalizedTitle { get; set; } = "";
    public string ServiceArea { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public bool IsOpen { get; set; } = true;
}
