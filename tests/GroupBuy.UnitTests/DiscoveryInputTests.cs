using GroupBuy.Application;
using GroupBuy.Domain;
using GroupBuy.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GroupBuy.UnitTests;

public sealed class DiscoveryInputTests
{
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "synthetic";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Theory]
    [InlineData("  rice\t flour\n", "rice flour")]
    [InlineData("ＲＩＣＥ", "RICE")]
    [InlineData("%_'\\", "%_'\\")]
    public void Query_normalizes_whitespace_and_unicode_without_interpreting_patterns(string input, string expected) => Assert.Equal(expected, DiscoveryInput.Query(input));
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("r")] [InlineData("rice\0")]
    public void Invalid_queries_are_rejected(string? input) => Assert.Equal(400, Assert.Throws<BusinessException>(() => DiscoveryInput.Query(input)).StatusCode);
    [Fact]
    public void Oversized_query_is_rejected() => Assert.Throws<BusinessException>(() => DiscoveryInput.Query(new string('x', 257)));
    [Theory]
    [InlineData("contact@example.invalid")]
    [InlineData("call 9999999999")]
    [InlineData("<script>rice</script>")]
    [InlineData("https://example.invalid")]
    public void Public_demand_title_rejects_obvious_contact_details_links_and_markup(string input) => Assert.Throws<BusinessException>(() => DiscoveryInput.PublicTitle(input));

    private static Dictionary<string, string?> Configuration() => new()
    {
        ["Pilot:Approved"] = "true", ["Pilot:ServiceAreas:0:Id"] = "synthetic", ["Pilot:ServiceAreas:0:Name"] = "Synthetic area",
        ["Pilot:ServiceAreas:0:PostalCodes:0"] = "900001"
    };
    [Theory]
    [InlineData("Pilot:Approved", "false")]
    [InlineData("Pilot:ServiceAreas:0:Id", "*")]
    [InlineData("Pilot:ServiceAreas:0:PostalCodes:0", "TEST")]
    [InlineData("Pilot:ServiceAreas:0:Name", "")]
    public void Inconsistent_production_area_configuration_fails_closed(string key, string value)
    {
        var values = Configuration(); values[key] = value;
        var areas = new DiscoveryAreas(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Environment());
        Assert.Equal(503, Assert.Throws<BusinessException>(() => areas.Resolve(new())).StatusCode);
    }
    [Fact]
    public void Missing_production_area_configuration_fails_closed()
    {
        var areas = new DiscoveryAreas(new ConfigurationBuilder().Build(), new Environment());
        Assert.Equal(503, Assert.Throws<BusinessException>(() => areas.Resolve(new())).StatusCode);
    }
    [Fact]
    public void Discovery_area_approval_does_not_require_or_enable_the_paid_pilot()
    {
        var values = Configuration(); values["Pilot:Approved"] = "false"; values["Discovery:ServiceAreasApproved"] = "true";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.True(new DiscoveryAreas(config, new Environment()).Resolve(new()).Supported);
        Assert.False(config.GetValue("Pilot:Enabled", false)); Assert.False(config.GetValue("Payments:Enabled", false));
    }
    [Theory]
    [InlineData("900001", true)] [InlineData("900002", false)]
    public void Only_configured_postal_code_is_supported(string code, bool supported)
    {
        var areas = new DiscoveryAreas(new ConfigurationBuilder().AddInMemoryCollection(Configuration()).Build(), new Environment());
        Assert.Equal(supported, areas.Resolve(new(PostalCode: code)).Supported);
    }
    [Theory]
    [InlineData("123")] [InlineData("000000")] [InlineData("abcdef")] [InlineData("900001%")]
    public void Postal_code_input_is_validated(string code)
    {
        var areas = new DiscoveryAreas(new ConfigurationBuilder().AddInMemoryCollection(Configuration()).Build(), new Environment());
        Assert.Equal(400, Assert.Throws<BusinessException>(() => areas.Resolve(new(PostalCode: code))).StatusCode);
    }
}
