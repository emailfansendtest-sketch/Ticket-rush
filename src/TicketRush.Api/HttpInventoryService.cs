using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using TicketRush.Domain;
using TicketRush.Inventory;

namespace TicketRush.Api;

/// <summary>
/// Transitional HTTP adapter. Only the authenticated buyer's bearer token is
/// forwarded, on a fresh request. Inventory independently validates the JWT.
/// No cross-service DbContext or transaction is shared on these operations.
/// </summary>
public sealed class HttpInventoryService(HttpClient client, IHttpContextAccessor context) : IInventoryService
{
    public async Task<IReadOnlyList<SeatSummary>> ListSeatsAsync(Guid eventId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"events/{eventId:D}/seats", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SeatSummary[]>(cancellationToken: ct)
            ?? throw new InvalidDataException("Inventory returned no seat data.");
    }

    public async Task<HoldView?> GetHoldAsync(Guid holdId, Guid buyerId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"holds/{holdId:D}");
        ForwardAuthorization(request);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var hold = await response.Content.ReadFromJsonAsync<HoldView>(cancellationToken: ct)
            ?? throw new InvalidDataException("Inventory returned no hold data.");
        // Do not trust an upstream record with another buyer ID.
        if (hold.BuyerId != buyerId) throw new InvalidDataException("Inventory returned the wrong owner.");
        return hold;
    }

    public async Task<OperationResult<HoldView>> HoldAsync(Guid eventId, Guid buyerId,
        string? idempotencyKey, string[]? requestedCodes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"events/{eventId:D}/holds")
        {
            Content = JsonContent.Create(new
            {
                BuyerId = buyerId, IdempotencyKey = idempotencyKey,
                SeatCodes = requestedCodes
            })
        };
        ForwardAuthorization(request);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var hold = await response.Content.ReadFromJsonAsync<HoldView>(cancellationToken: ct)
                ?? throw new InvalidDataException("Inventory returned no hold data.");
            if (hold.BuyerId != buyerId || hold.EventId != eventId)
                throw new InvalidDataException("Inventory returned a hold with inconsistent identity.");
            return new(response.StatusCode == HttpStatusCode.Created
                ? ResultKind.Created : ResultKind.Replayed, hold);
        }
        var kind = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ResultKind.Invalid,
            HttpStatusCode.NotFound => ResultKind.NotFound,
            HttpStatusCode.Conflict => ResultKind.Conflict,
            _ => throw new HttpRequestException(
                $"Unexpected Inventory status: {(int)response.StatusCode} ({response.StatusCode})",
                null, response.StatusCode)
        };
        // Preserve stable result status; never require an upstream error body.
        return new(kind, Error: $"Inventory rejected reservation ({(int)response.StatusCode}).");
    }

    private void ForwardAuthorization(HttpRequestMessage request)
    {
        var upstream = context.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(upstream) ||
            !AuthenticationHeaderValue.TryParse(upstream, out var parsed) ||
            !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authenticated bearer token missing from request context.");
        request.Headers.Authorization = parsed;
    }
}
