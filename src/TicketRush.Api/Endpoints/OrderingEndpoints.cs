using Microsoft.AspNetCore.Mvc;
using TicketRush.Ordering;

namespace TicketRush.Api.Endpoints;

public static class OrderingEndpoints
{
    public static IEndpointRouteBuilder MapOrderingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/{orderId:guid}", GetOrderAsync);

        endpoints.MapPost("/orders", async (
            ConfirmOrderRequest request, IOrderingService ordering,
            CancellationToken ct) =>
        {
            var result = await ordering.ConfirmAsync(
                request.HoldId, request.BuyerId, request.IdempotencyKey, ct);
            return BookingResultMapper.MapResult(result,
                v => $"/orders/{v.Id:D}?buyerId={v.BuyerId:D}");
        });
        return endpoints;
    }

    private static async Task<IResult> GetOrderAsync(
        Guid orderId, [FromQuery] Guid buyerId,
        IOrderingService ordering, CancellationToken ct)
    {
        if (buyerId == Guid.Empty)
            return Results.BadRequest(new { error = "Provide a buyer ID." });

        var order = await ordering.GetOrderAsync(orderId, buyerId, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}

public sealed record ConfirmOrderRequest(
    Guid HoldId, Guid BuyerId, string IdempotencyKey);
