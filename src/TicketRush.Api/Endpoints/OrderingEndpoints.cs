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

    /// <summary>
    /// Retire the legacy transaction-spanning routes in the distributed deployment.
    /// Do not forward these requests to /checkouts: checkout requires a signed hold
    /// proof and a different asynchronous response contract (HTTP 202).
    /// </summary>
    public static IEndpointRouteBuilder MapRetiredLegacyOrderingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        static IResult Retired() => Results.Json(new
        {
            error = "Legacy synchronous checkout has been retired.",
            replacement = "POST /checkouts on the Ordering API using a signed hold proof",
            guidance = "Use the HoldProof returned when creating the hold."
        }, statusCode: StatusCodes.Status410Gone);

        endpoints.MapPost("/orders", Retired).RequireAuthorization();
        endpoints.MapGet("/orders/{orderId:guid}", Retired).RequireAuthorization();
        return endpoints;
    }

}

public sealed record ConfirmOrderRequest(
    Guid HoldId, Guid BuyerId, string IdempotencyKey);
