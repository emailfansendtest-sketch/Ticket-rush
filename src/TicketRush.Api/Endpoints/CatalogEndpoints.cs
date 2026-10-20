using TicketRush.Catalog;

namespace TicketRush.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/events", async (IEventCatalog catalog, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await catalog.ListEventsAsync(ct));
            }
            catch (HttpRequestException)
            {
                // Network failures and upstream HTTP errors are dependency failures,
                // not an unexpected error in the Booking API itself.
                return Results.Problem(
                    title: "Catalog service unavailable",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout, not the caller disconnecting.
                return Results.Problem(
                    title: "Catalog service timed out",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });
        return endpoints;
    }
}
