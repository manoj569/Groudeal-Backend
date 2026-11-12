using System.Linq.Expressions;
namespace GroupBuy.Domain;

// Read-only UI classification, never persisted or used to mutate commercial state.
public enum ParticipationStatus { PaymentPending, PaymentFailed, Active, Confirmed, Ready, Completed, Cancelled, Refunded }
public class ParticipationFacts
{
    public bool FullyRefunded { get; init; }
    public bool Fulfilled { get; init; }
    public bool Cancelled { get; init; }
    public bool PaymentFailed { get; init; }
    public bool Qualified { get; init; }
    public bool Ready { get; init; }
    public bool Confirmed { get; init; }
}
public static class ParticipationRules
{
    // This exact expression runs in SQL for filtering/counts and in unit tests.
    public static readonly Expression<Func<ParticipationFacts,ParticipationStatus>> Status = f =>
        f.FullyRefunded ? ParticipationStatus.Refunded :
        f.Fulfilled ? ParticipationStatus.Completed :
        f.Cancelled ? ParticipationStatus.Cancelled :
        f.PaymentFailed ? ParticipationStatus.PaymentFailed :
        !f.Qualified ? ParticipationStatus.PaymentPending :
        f.Ready ? ParticipationStatus.Ready :
        f.Confirmed ? ParticipationStatus.Confirmed : ParticipationStatus.Active;
}
