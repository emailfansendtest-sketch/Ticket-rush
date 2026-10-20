using TicketRush.Inventory;

namespace TicketRush.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/events/{eventId:guid}/seats", async (
            Guid eventId, IInventoryService inventory, CancellationToken ct) =>
            await inventory.ListSeatsAsync(eventId, ct));

        endpoints.MapPost("/events/{eventId:guid}/holds", async (
            Guid eventId, CreateHoldRequest request, IInventoryService inventory,
            CancellationToken ct) =>
        {
            var result = await inventory.HoldAsync(eventId, request.BuyerId,
                request.IdempotencyKey, request.SeatCodes, ct);
            return BookingResultMapper.MapResult(result, v => $"/holds/{v.Id}");
        });

        return endpoints;
    }
}

public sealed record CreateHoldRequest(Guid BuyerId, string IdempotencyKey, string[] SeatCodes);
