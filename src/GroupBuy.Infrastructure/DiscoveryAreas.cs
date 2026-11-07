using System.Text.RegularExpressions;
using GroupBuy.Application;
using GroupBuy.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace GroupBuy.Infrastructure;

public sealed class DiscoveryAreas(IConfiguration config, IHostEnvironment environment)
{
    public DiscoveryLocation Resolve(DiscoveryFilter filter)
    {
        var postal = filter.PostalCode?.Trim();
        if (postal is not null && !Regex.IsMatch(postal, "^[1-9][0-9]{5}$", RegexOptions.CultureInvariant))
            throw new BusinessException(400, "invalid_postal_code", "Use a six-digit postal code.");
        if (filter.ServiceArea is not null && !Regex.IsMatch(filter.ServiceArea, "^[a-z0-9-]{1,80}$", RegexOptions.CultureInvariant))
            throw new BusinessException(400, "invalid_service_area", "Use a configured service-area identifier.");
        var areas = PilotConfiguration.Areas(config);
        if (areas.Length is < 1 or > 10 || (environment.IsProduction() && !config.GetValue("Pilot:Approved", false) && !config.GetValue("Discovery:ServiceAreasApproved", false))
            || areas.Any(a => a is null || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 100
                || !Regex.IsMatch(a.Id ?? "", "^[a-z0-9-]{1,80}$") || a.PostalCodes is null || a.PostalCodes.Length is < 1 or > 30
                || a.PostalCodes.Any(p => p is null || !Regex.IsMatch(p, "^[1-9][0-9]{5}$")))
            || areas.Select(a => a.Id).Distinct().Count() != areas.Length
            || areas.SelectMany(a => a.PostalCodes).Distinct().Count() != areas.Sum(a => a.PostalCodes.Length))
            throw new BusinessException(503, "service_area_unconfigured", "Approved service-area configuration is unavailable.");
        if (postal is null && filter.ServiceArea is null && areas.Length > 1)
            return new("selection_required", false, null, null, null);
        var area = areas.SingleOrDefault(a => (filter.ServiceArea is null || a.Id == filter.ServiceArea)
            && (postal is null || a.PostalCodes.Contains(postal)));
        return area is null ? new("unsupported", false, postal, null, null)
            : new("supported", true, postal, area.Id, area.Name);
    }

    public string[] PostalCodes(DiscoveryLocation location) => location.Supported
        ? PilotConfiguration.Areas(config).Single(a => a.Id == location.ServiceArea).PostalCodes : [];
}
