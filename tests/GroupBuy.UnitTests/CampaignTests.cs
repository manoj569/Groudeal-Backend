using GroupBuy.Application;
using GroupBuy.Domain;

namespace GroupBuy.UnitTests;
public class CampaignTests
{
    private static GroupBuyCampaign Open() => new() { Status = CampaignStatus.Open, TargetQuantity = 10, MinimumReservationQuantity = 1, MaximumReservationQuantity = 10, StartsAtUtc = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(11)] [InlineData(1.0001)]
    public void Invalid_quantities_rejected(decimal quantity) => Assert.Throws<BusinessException>(() => CampaignRules.ValidateReservation(Open(), quantity, UnitOfMeasure.Kilogram, DateTimeOffset.UtcNow));
    [Fact] public void Expired_campaign_rejects_join() { var c = Open(); c.ExpiresAtUtc = DateTimeOffset.UtcNow; Assert.Throws<BusinessException>(() => CampaignRules.ValidateReservation(c, 1, UnitOfMeasure.Kilogram, DateTimeOffset.UtcNow)); }
    [Fact] public void Capacity_rejected() { var c = Open(); c.ReservedQuantity = 9; Assert.Throws<BusinessException>(() => CampaignRules.ValidateReservation(c, 2, UnitOfMeasure.Kilogram, DateTimeOffset.UtcNow)); }
    [Fact] public void Discrete_units_reject_fraction() => Assert.Throws<BusinessException>(() => CampaignRules.ValidateReservation(Open(), 1.5m, UnitOfMeasure.Piece, DateTimeOffset.UtcNow));
    [Fact] public void Completed_is_terminal() { var c = Open(); c.Status = CampaignStatus.Completed; foreach (var state in Enum.GetValues<CampaignStatus>()) Assert.False(CampaignRules.CanTransition(c.Status, state)); }
    [Fact] public void Target_reached_can_reopen_after_cancellation() { var c = Open(); c.TransitionTo(CampaignStatus.TargetReached); c.TransitionTo(CampaignStatus.Open); Assert.Equal(CampaignStatus.Open, c.Status); }
    [Fact] public void Fulfillment_requires_confirmation() { var c = Open(); Assert.Throws<BusinessException>(() => c.TransitionTo(CampaignStatus.FulfillmentInProgress)); }
    [Theory] [InlineData("{}", true)] [InlineData("{\"color\":\"Black\"}", true)] [InlineData("{\"sql\":\"DROP TABLE\"}", false)] [InlineData("[]", false)] [InlineData("{\"size\":42}", false)] [InlineData("{\"size\":\"M\",\"size\":\"L\"}", false)]
    public void Attributes_are_constrained(string json, bool valid) => Assert.Equal(valid, ProductValidator.ValidAttributes(json));
    [Theory] [InlineData(-1)] [InlineData(0)] [InlineData(101)] [InlineData(1.001)]
    public void Invalid_campaign_price(decimal price) { var r = new CampaignRequest(Guid.NewGuid(), Guid.NewGuid(), "Title", "Description", 100, price, 10, 1, 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)); Assert.False(new CampaignValidator().Validate(r).IsValid); }
    [Fact] public void Registration_requires_valid_email_and_password() => Assert.False(new RegisterValidator().Validate(new RegisterRequest("invalid", "short", "")).IsValid);
}
