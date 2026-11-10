using GroupBuy.Domain;
namespace GroupBuy.UnitTests;
public sealed class CheckoutTests
{
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(11)] [InlineData(1.0001)]
    public void Quantities_outside_limits_reject(decimal quantity) => Assert.Equal("invalid_quantity",Assert.Throws<BusinessException>(() => CheckoutRules.ValidateQuantity(quantity,1,10,UnitOfMeasure.Kilogram)).Code);
    [Theory] [InlineData(UnitOfMeasure.Piece)] [InlineData(UnitOfMeasure.Pack)]
    public void Discrete_packages_reject_fractions(UnitOfMeasure unit) => Assert.Throws<BusinessException>(() => CheckoutRules.ValidateQuantity(1.5m,1,10,unit));
    [Fact] public void Bulk_quantities_allow_supported_precision() => CheckoutRules.ValidateQuantity(.125m,.1m,10,UnitOfMeasure.Kilogram);
    [Theory]
    [InlineData(PaymentState.Creating,false,false,false,"processing")]
    [InlineData(PaymentState.Pending,false,false,false,"processing")]
    [InlineData(PaymentState.Authorized,false,false,false,"pending_verification")]
    [InlineData(PaymentState.Captured,true,true,false,"successful")]
    [InlineData(PaymentState.Captured,false,false,false,"processing")]
    [InlineData(PaymentState.Pending,false,true,false,"expired")]
    [InlineData(PaymentState.Expired,false,false,false,"expired")]
    [InlineData(PaymentState.Failed,false,false,false,"failed")]
    [InlineData(PaymentState.RefundPending,false,true,false,"refund_pending")]
    [InlineData(PaymentState.Refunded,false,true,true,"refunded")]
    [InlineData(PaymentState.Captured,true,true,true,"refund_completed")]
    public void Status_preserves_verified_payment_refunds_and_expiry(PaymentState state,bool participated,bool expired,bool refund,string status)
        => Assert.Equal(status,CheckoutRules.Status(state,participated,expired,refund).Status);
    [Fact] public void Authorization_is_not_success() => Assert.Equal("payment_pending",CheckoutRules.Status(PaymentState.Authorized,false,false,false).Code);
}
