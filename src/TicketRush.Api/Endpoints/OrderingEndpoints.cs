using TicketRush.Ordering;

namespace TicketRush.Api.Endpoints;

public static class OrderingEndpoints
{
    public static IEndpointRouteBuilder MapOrderingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders", async (
            ConfirmOrderRequest request, IOrderingService ordering,
            CancellationToken ct) =>
        {
            var result = await ordering.ConfirmAsync(
                request.HoldId, request.BuyerId, request.IdempotencyKey, ct);
            return BookingResultMapper.MapResult(result, v => $"/orders/{v.Id}");
        });
        return endpoints;
    }
}

public sealed record ConfirmOrderRequest(
    Guid HoldId, Guid BuyerId, string IdempotencyKey);
