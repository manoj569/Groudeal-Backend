namespace GroupBuy.Domain;

public static class CheckoutRules
{
    public static void ValidateQuantity(decimal quantity, decimal minimum, decimal maximum, UnitOfMeasure unit)
    {
        if (quantity <= 0 || quantity < minimum || quantity > maximum || decimal.Round(quantity,3) != quantity
            || (unit is UnitOfMeasure.Piece or UnitOfMeasure.Pack && quantity != decimal.Truncate(quantity)))
            throw new BusinessException(400,"invalid_quantity","Quantity is outside the supported package limits.");
    }

    public static (string Status, string Code) Status(PaymentState state, bool participated, bool expired, bool refundsComplete)
        => state switch
        {
            PaymentState.RefundPending => ("refund_pending","refund_pending"),
            PaymentState.Refunded => ("refunded","refund_completed"),
            _ when refundsComplete => ("refund_completed","refund_completed"),
            PaymentState.Captured when participated => ("successful","payment_already_completed"),
            PaymentState.Failed => ("failed","payment_failed"),
            PaymentState.Expired => ("expired","reservation_expired"),
            _ when expired && !participated => ("expired","reservation_expired"),
            PaymentState.Authorized => ("pending_verification","payment_pending"),
            _ => ("processing","payment_pending")
        };
}
