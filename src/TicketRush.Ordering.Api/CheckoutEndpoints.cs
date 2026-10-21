using Microsoft.AspNetCore.Mvc;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.Api;

public sealed record StartCheckoutRequest(Guid HoldId, Guid BuyerId, string? IdempotencyKey);

public static class CheckoutEndpoints
{
    public static IEndpointRouteBuilder MapCheckoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/checkouts", StartCheckoutAsync);
        endpoints.MapGet("/checkouts/{orderId:guid}", GetCheckoutAsync);
        return endpoints;
    }

    private static async Task<IResult> StartCheckoutAsync(
        StartCheckoutRequest request,
        CheckoutIntakeService intake,
        CheckoutStatusReader reader,
        CancellationToken ct)
    {
        var admitted = await intake.AcceptAsync(
            request.HoldId, request.BuyerId, request.IdempotencyKey, ct);
        if (admitted.Kind == CheckoutAdmissionKind.Invalid)
            return Results.BadRequest(new { error = "Invalid checkout: holdId, buyerId, and a nonblank idempotencyKey of up to 100 characters are required." });
        if (admitted.Kind == CheckoutAdmissionKind.Conflict)
            return Results.Conflict(new { error = "Idempotency key or hold already belongs to another checkout." });

        // The transaction has already committed before any HTTP success.
        // Replayed requests return the latest known saga state, not stale intake state.
        var status = await reader.GetAsync(admitted.OrderId!.Value, request.BuyerId, ct)
            ?? throw new InvalidOperationException("Accepted checkout was not found in Ordering storage.");
        var location = $"/checkouts/{status.OrderId:D}?buyerId={request.BuyerId:D}";
        return admitted.Kind == CheckoutAdmissionKind.Accepted
            ? Results.Accepted(location, status)
            : Results.Ok(status);
    }

    private static async Task<IResult> GetCheckoutAsync(
        Guid orderId, [FromQuery] Guid? buyerId,
        CheckoutStatusReader reader, HttpContext http, CancellationToken ct)
    {
        if (buyerId is null || buyerId == Guid.Empty)
            return Results.BadRequest(new { error = "A nonempty buyerId query parameter is required." });
        http.Response.Headers.CacheControl = "no-store";
        var status = await reader.GetAsync(orderId, buyerId.Value, ct);
        // Unknown order and mismatched buyer have indistinguishable results.
        return status is null ? Results.NotFound() : Results.Ok(status);
    }
}
