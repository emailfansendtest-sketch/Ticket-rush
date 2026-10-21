using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
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

        endpoints.MapGet("/holds/{holdId:guid}", GetHoldAsync).RequireAuthorization();

        endpoints.MapPost("/events/{eventId:guid}/holds", async (
            Guid eventId, CreateHoldRequest request, IInventoryService inventory,
            HoldCheckoutProof proof, HttpContext http, CancellationToken ct) =>
        {
            Guid? authenticatedBuyer = BuyerIdentity.FromPrincipal(http.User);
            if (authenticatedBuyer is null || authenticatedBuyer.Value != request.BuyerId)
                return Results.Forbid();
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
        }).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> GetHoldAsync(
        Guid holdId, [FromQuery] Guid? buyerId, ClaimsPrincipal user,
        IInventoryService inventory, CancellationToken ct)
    {
        Guid? authenticatedBuyer = BuyerIdentity.FromPrincipal(user);
        if (authenticatedBuyer is null || (buyerId.HasValue && buyerId.Value != authenticatedBuyer.Value))
            return Results.NotFound();
        var hold = await inventory.GetHoldAsync(holdId, authenticatedBuyer.Value, ct);
        return hold is null ? Results.NotFound() : Results.Ok(hold);
    }
}

public sealed record CreateHoldRequest(Guid BuyerId, string IdempotencyKey, string[] SeatCodes);

/// <summary>Returned only by hold POST; never by public GET /holds.</summary>
public sealed record HoldReceipt(Guid Id, Guid EventId, Guid BuyerId, string[] SeatCodes,
    DateTimeOffset ExpiresAtUtc, string Status, string HoldProof);
