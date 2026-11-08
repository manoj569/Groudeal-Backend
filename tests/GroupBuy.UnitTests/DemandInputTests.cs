using GroupBuy.Application;
using GroupBuy.Domain;
namespace GroupBuy.UnitTests;

public sealed class DemandInputTests
{
    private static readonly Guid Category = Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static CreateDemandRequest Product() => new("  Organic   rice  ", "synthetic", Category, DemandType.Product, null, 10, " KG ");
    [Fact]
    public void Legacy_input_retains_product_compatibility_without_inventing_units()
    { var r = DemandInput.Normalize(new("Local rice", "synthetic")); Assert.Equal(DemandType.Product, r.DemandType); Assert.Null(r.Quantity); Assert.Null(r.Unit); }
    [Fact]
    public void Normalization_is_nfkc_collapsed_and_deterministic()
    { var r = DemandInput.Normalize(Product() with { Title = "  ＲＩＣＥ\t  flour ", Details = "  Public   specification ", Requirement = "  5 kg   packs " }); Assert.Equal("RICE flour", r.Title); Assert.Equal("kg", r.Unit); Assert.Equal("Public specification", r.Details); Assert.Equal("5 kg packs", r.Requirement); }
    [Fact]
    public void Different_quantities_details_and_case_share_aggregate_identity()
    { var a = DemandInput.Normalize(Product()); var b = DemandInput.Normalize(Product() with { Title = "ORGANIC RICE", Quantity = 20, Details = "Different packs" }); Assert.Equal(DemandInput.Canonical(a), DemandInput.Canonical(b)); }
    [Fact]
    public void Product_identity_ignores_display_title()
    { var a = DemandInput.Normalize(Product() with { ProductId = Guid.NewGuid() }); Assert.Equal(DemandInput.Canonical(a), DemandInput.Canonical(a with { Title = "A different display name" }));
        Assert.Equal(new string('x', 99), DemandInput.CatalogTitle(new string('x', 99) + "\U0001F33E"));
        Assert.Equal(new string('x', 98) + "\U0001F33E", DemandInput.CatalogTitle(new string('x', 98) + "\U0001F33Eextra"));
        Assert.Equal("RICE flour", DemandInput.CatalogTitle("  ＲＩＣＥ\t flour  "));
        Assert.Throws<BusinessException>(() => DemandInput.CatalogTitle("\uD800")); }
    [Fact]
    public void Canonical_scope_includes_type_category_area_and_product()
    { var a = DemandInput.Normalize(Product()); foreach (var b in new[] { a with { CategoryId = Guid.NewGuid() }, a with { ServiceArea = "other" }, a with { DemandType = DemandType.Service }, a with { ProductId = Guid.NewGuid() } }) Assert.NotEqual(DemandInput.Canonical(a), DemandInput.Canonical(b)); }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000001)]
    [InlineData(1.0001)]
    public void Invalid_product_quantities_are_rejected(decimal quantity) => Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { Quantity = quantity }));
    [Theory]
    [InlineData("household")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Product_unit_must_be_explicit_supported(string unit) => Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { Unit = unit }));
    [Fact]
    public void Service_can_use_a_requirement_without_fake_quantity()
    { var r = DemandInput.Normalize(new("Home solar installation", "synthetic", Category, DemandType.Service, Requirement: "Assess a roof installation")); Assert.Null(r.Quantity); Assert.Null(r.Unit); }
    [Theory]
    [InlineData("household")]
    [InlineData("project")]
    [InlineData("hour")]
    [InlineData("visit")]
    [InlineData("square_metre")]
    public void Service_supports_explicit_meaningful_units(string unit) => Assert.Equal(unit, DemandInput.Normalize(new("Home service", "synthetic", Category, DemandType.Service, Quantity: 2, Unit: unit)).Unit);
    [Fact] public void Service_cannot_link_physical_catalog() => Assert.Throws<BusinessException>(() => DemandInput.Normalize(new("Home service", "synthetic", Category, DemandType.Service, Guid.NewGuid())));
    [Fact] public void Unknown_type_cannot_be_spoofed() => Assert.Equal("invalid_demand_type", Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { DemandType = (DemandType)99 })).Code);
    [Fact]
    public void Rich_requests_require_type_category_and_product_quantity()
    { foreach (var r in new[] { Product() with { DemandType = null }, Product() with { CategoryId = null }, Product() with { Quantity = null, Unit = null } }) Assert.Throws<BusinessException>(() => DemandInput.Normalize(r)); }
    [Theory]
    [InlineData("<script>bad</script>")]
    [InlineData("contact@example.invalid")]
    [InlineData("https://example.invalid")]
    [InlineData("call 9999999999")]
    [InlineData("bad\0text")]
    public void Optional_text_rejects_markup_contact_links_and_controls(string text) => Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { Details = text }));
    [Fact]
    public void Optional_lengths_are_bounded()
    { Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { Details = new string('x', 1001) })); Assert.Throws<BusinessException>(() => DemandInput.Normalize(Product() with { Requirement = new string('x', 501) })); }
    [Fact]
    public void Progress_has_no_fabricated_target_or_percentage()
    { var p = new DemandProgress(23); Assert.Equal(23, p.CurrentInterest); Assert.Null(p.TargetInterest); Assert.Null(p.ProgressPercentage); }
    [Fact]
    public void Existing_open_and_cancelled_fields_define_explicit_lifecycle()
    { var d = new DiscoveryDemand(); Assert.Equal(DemandStatus.BuildingInterest, d.Status); d.IsOpen = false; Assert.Equal(DemandStatus.Closed, d.Status); d.IsCancelled = true; Assert.Equal(DemandStatus.Cancelled, d.Status); }
    [Theory]
    [InlineData(99, null)]
    [InlineData(null, 99)]
    public void Unknown_filter_enums_are_rejected(int? type, int? status) => Assert.Throws<BusinessException>(() => DemandInput.ValidateFilter(new(status.HasValue ? (DemandStatus)status : null, type.HasValue ? (DemandType)type : null)));
}
