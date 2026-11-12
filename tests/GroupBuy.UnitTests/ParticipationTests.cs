using GroupBuy.Domain;
namespace GroupBuy.UnitTests;
public sealed class ParticipationTests
{
    [Theory]
    [InlineData(false,false,false,false,false,false,false,ParticipationStatus.PaymentPending)]
    [InlineData(false,false,false,true,false,false,false,ParticipationStatus.PaymentFailed)]
    [InlineData(false,false,false,false,true,false,false,ParticipationStatus.Active)]
    [InlineData(false,false,false,false,true,false,true,ParticipationStatus.Confirmed)]
    [InlineData(false,false,false,false,true,true,true,ParticipationStatus.Ready)]
    [InlineData(false,true,true,false,true,false,true,ParticipationStatus.Completed)]
    [InlineData(false,false,true,false,true,true,true,ParticipationStatus.Cancelled)]
    [InlineData(true,true,true,false,true,true,true,ParticipationStatus.Refunded)]
    [InlineData(false,false,false,false,false,true,true,ParticipationStatus.PaymentPending)]
    public void Module6_status_precedence(bool refunded,bool fulfilled,bool cancelled,bool failed,bool qualified,bool ready,bool confirmed,ParticipationStatus expected)
        => Assert.Equal(expected,ParticipationRules.Status.Compile()(new() { FullyRefunded=refunded,Fulfilled=fulfilled,Cancelled=cancelled,PaymentFailed=failed,Qualified=qualified,Ready=ready,Confirmed=confirmed }));
}
