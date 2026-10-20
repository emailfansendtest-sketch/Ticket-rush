using Microsoft.AspNetCore.Mvc;
using TicketRush.Inventory;

namespace TicketRush.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/events/{eventId:guid}/seats", async (
            Guid eventId, IInventoryService inventory, CancellationToken ct) =>
            await inventory.ListSeatsAsync(eventId, ct));

        endpoints.MapGet("/holds/{holdId:guid}", GetHoldAsync);

        endpoints.MapPost("/events/{eventId:guid}/holds", async (
            Guid eventId, CreateHoldRequest request, IInventoryService inventory,
            CancellationToken ct) =>
        {
            var result = await inventory.HoldAsync(eventId, request.BuyerId,
                request.IdempotencyKey, request.SeatCodes, ct);
            return BookingResultMapper.MapResult(result,
                v => $"/holds/{v.Id:D}?buyerId={v.BuyerId:D}");
        });

        return endpoints;
    }

    private static async Task<IResult> GetHoldAsync(
        Guid holdId, [FromQuery] Guid buyerId,
        IInventoryService inventory, CancellationToken ct)
    {
        if (buyerId == Guid.Empty)
            return Results.BadRequest(new { error = "Provide a buyer ID." });

        var hold = await inventory.GetHoldAsync(holdId, buyerId, ct);
        return hold is null ? Results.NotFound() : Results.Ok(hold);
    }
}

public sealed record CreateHoldRequest(Guid BuyerId, string IdempotencyKey, string[] SeatCodes);
