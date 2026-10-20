using TicketRush.Catalog;

namespace TicketRush.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/events", async (IEventCatalog catalog, CancellationToken ct) =>
            await catalog.ListEventsAsync(ct));
        return endpoints;
    }
}
