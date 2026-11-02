using GroupBuy.Application;
using GroupBuy.Domain;

namespace GroupBuy.UnitTests;

public class OperationsTests
{
    [Theory]
    [InlineData(-1, 0, UnitOfMeasure.Kilogram)]
    [InlineData(1.0001, 0, UnitOfMeasure.Kilogram)]
    [InlineData(2, 3, UnitOfMeasure.Kilogram)]
    [InlineData(1.5, 0, UnitOfMeasure.Piece)]
    [InlineData(1.5, 0, UnitOfMeasure.Pack)]
    [InlineData(1000001, 0, UnitOfMeasure.Litre)]
    public void Invalid_inventory_is_rejected(decimal available, decimal committed, UnitOfMeasure unit) => Assert.Throws<BusinessException>(() => InventoryRules.Validate(available, committed, unit));

    [Theory]
    [InlineData(UnitOfMeasure.Kilogram)] [InlineData(UnitOfMeasure.Litre)]
    public void Bulk_inventory_preserves_fractional_variant_units(UnitOfMeasure unit) => InventoryRules.Validate(1.125m, 1.001m, unit);

    [Fact]
    public void Required_regex_fields_reject_null()
    {
        Assert.False(new ReservationValidator().Validate(new ReservationRequest(1, null!)).IsValid);
        Assert.False(new LocationValidator().Validate(new LocationRequest("Road", null, "Area", "City", "State", null!, null, null)).IsValid);
        Assert.False(new SellerValidator().Validate(new SellerRequest("Shop", SellerType.Grocery, "", "a@example.invalid", null!, null, new("Road", null, "Area", "City", "State", "110001", null, null))).IsValid);
        Assert.False(new ProductValidator().Validate(new ProductRequest(Guid.NewGuid(), "Product", null!, "", ProductType.Physical, "SKU", "Variant", UnitOfMeasure.Piece, 1, "{}")).IsValid);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("123456")] [InlineData("abcdefghijklmnopqrstuvwxyz123456")]
    public void Pickup_code_contract_rejects_malformed_codes(string? code) => Assert.False(new PickupCodeValidator().Validate(new PickupCodeRequest(code!)).IsValid);

    [Theory]
    [InlineData(null)] [InlineData("short")] [InlineData("invalid key!")]
    public void Confirmation_requires_valid_idempotency_key(string? key) => Assert.False(new DecisionValidator().Validate(new DecisionRequest(key!, null)).IsValid);
}

