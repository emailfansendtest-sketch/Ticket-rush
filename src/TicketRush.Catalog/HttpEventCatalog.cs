using System.Net.Http.Json;

namespace TicketRush.Catalog;

/// <summary>
/// Booking API's read-only adapter to the independently deployed Catalog HTTP service.
/// It does not access the Catalog database or another service's EF entities.
/// </summary>
public sealed class HttpEventCatalog(HttpClient client) : IEventCatalog
{
    public async Task<IReadOnlyList<EventSummary>> ListEventsAsync(CancellationToken ct)
    {
        using var response = await client.GetAsync("events", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EventSummary[]>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Catalog returned an empty JSON response.");
    }
}
