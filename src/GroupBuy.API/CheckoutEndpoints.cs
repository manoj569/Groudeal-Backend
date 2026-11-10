using System.Security.Claims;
using GroupBuy.Application;
namespace GroupBuy.API;
public static class CheckoutEndpoints
{
    private static Actor Customer(HttpContext c) => new(Guid.Parse(c.User.FindFirstValue("sub")!), false, c.TraceIdentifier);
    public static void MapCheckout(this WebApplication app)
    {
        var customer = app.MapGroup("/api/v1").RequireAuthorization("Customer").WithTags("Checkout");
        customer.MapPost("/group-buys/{id:guid}/checkout/quote", (Guid id, CheckoutQuoteRequest r, HttpContext c, ICheckout s, CancellationToken ct) => s.Quote(Customer(c),id,r,ct)).RequireRateLimiting("checkout");
        customer.MapPost("/group-buys/{id:guid}/checkout/confirm", (Guid id, CheckoutConfirmRequest r, HttpContext c, ICheckout s, CancellationToken ct) => s.ConfirmCheckout(Customer(c),id,r,ct)).RequireRateLimiting("checkout");
        customer.MapPost("/group-buys/{id:guid}/checkout/recover", (Guid id, CheckoutConfirmRequest r, HttpContext c, ICheckout s, CancellationToken ct) => s.RecoverQuote(Customer(c),id,r,ct)).RequireRateLimiting("checkout");
        customer.MapGet("/me/checkouts/{id:guid}", (Guid id, HttpContext c, ICheckout s, CancellationToken ct) => s.CheckoutStatus(Customer(c),id,ct));
        customer.MapPost("/me/checkouts/{id:guid}/recover", (Guid id, HttpContext c, ICheckout s, CancellationToken ct) => s.RecoverCheckout(Customer(c),id,ct)).RequireRateLimiting("checkout");
    }
}
