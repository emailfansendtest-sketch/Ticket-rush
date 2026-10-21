using Microsoft.AspNetCore.Mvc;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Domain;
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
            HoldCheckoutProof proof, HttpContext http, CancellationToken ct) =>
        {
            var result = await inventory.HoldAsync(eventId, request.BuyerId,
                request.IdempotencyKey, request.SeatCodes, ct);
            if (result.Value is { } hold && (result.Kind is ResultKind.Created or ResultKind.Replayed))
            {
                http.Response.Headers.CacheControl = "no-store";
                var receipt = new HoldReceipt(hold.Id, hold.EventId, hold.BuyerId,
                    hold.SeatCodes, hold.ExpiresAtUtc, hold.Status,
                    proof.Issue(hold.Id, hold.BuyerId, hold.ExpiresAtUtc));
                return result.Kind == ResultKind.Created
                    ? Results.Created($"/holds/{hold.Id:D}?buyerId={hold.BuyerId:D}", receipt)
                    : Results.Ok(receipt);
            }
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

/// <summary>Returned only by hold POST; never by public GET /holds.</summary>
public sealed record HoldReceipt(Guid Id, Guid EventId, Guid BuyerId, string[] SeatCodes,
    DateTimeOffset ExpiresAtUtc, string Status, string HoldProof);
