using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Ordering;

namespace TicketRush.Api.Endpoints;

public static class OrderingEndpoints
{
    public static IEndpointRouteBuilder MapOrderingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/orders/{orderId:guid}", GetOrderAsync).RequireAuthorization();

        endpoints.MapPost("/orders", async (
            ConfirmOrderRequest request, ClaimsPrincipal user, IOrderingService ordering,
            CancellationToken ct) =>
        {
            Guid? buyer = BuyerIdentity.FromPrincipal(user);
            if (buyer is null || buyer.Value != request.BuyerId)
                return Results.Forbid();
            var result = await ordering.ConfirmAsync(
                request.HoldId, request.BuyerId, request.IdempotencyKey, ct);
            return BookingResultMapper.MapResult(result,
                v => $"/orders/{v.Id:D}?buyerId={v.BuyerId:D}");
        }).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> GetOrderAsync(
        Guid orderId, [FromQuery] Guid? buyerId, ClaimsPrincipal user,
        IOrderingService ordering, CancellationToken ct)
    {
        Guid? buyer = BuyerIdentity.FromPrincipal(user);
        if (buyer is null || (buyerId.HasValue && buyerId.Value != buyer.Value))
            return Results.NotFound();
        var order = await ordering.GetOrderAsync(orderId, buyer.Value, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}

public sealed record ConfirmOrderRequest(
    Guid HoldId, Guid BuyerId, string IdempotencyKey);
