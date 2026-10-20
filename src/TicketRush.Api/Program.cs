using Microsoft.EntityFrameworkCore;
using TicketRush.Api.Endpoints;
using TicketRush.Catalog;
using TicketRush.Domain;
using TicketRush.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
        ?? throw new InvalidOperationException("BookingDb connection string is missing.")));
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<IEventCatalog, EfEventCatalog>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health/live");

app.MapCatalogEndpoints();

app.MapGet("/events/{eventId:guid}/seats", async (Guid eventId, BookingDbContext db,
    CancellationToken ct) =>
    await db.Seats.AsNoTracking()
        .Where(x => x.EventId == eventId)
        .OrderBy(x => x.SeatCode)
        .Select(x => new { x.SeatCode, x.Status, x.HeldUntilUtc })
        .ToListAsync(ct));

app.MapPost("/events/{eventId:guid}/holds", async (Guid eventId, CreateHoldRequest request,
    BookingService booking, CancellationToken ct) =>
{
    var result = await booking.HoldAsync(eventId, request.BuyerId,
        request.IdempotencyKey, request.SeatCodes, ct);
    return MapResult(result, v => $"/holds/{v.Id}");
});

app.MapPost("/orders", async (ConfirmOrderRequest request,
    BookingService booking, CancellationToken ct) =>
{
    var result = await booking.ConfirmAsync(request.HoldId, request.BuyerId,
        request.IdempotencyKey, ct);
    return MapResult(result, v => $"/orders/{v.Id}");
});

app.Run();

static IResult MapResult<T>(OperationResult<T> result, Func<T, string> location) where T : class =>
    result.Kind switch
    {
        ResultKind.Created when result.Value is not null => Results.Created(location(result.Value), result.Value),
        ResultKind.Replayed when result.Value is not null => Results.Ok(result.Value),
        ResultKind.NotFound => Results.NotFound(new { error = result.Error }),
        ResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
        ResultKind.Expired => Results.Conflict(new { error = result.Error }),
        _ => Results.Conflict(new { error = result.Error })
    };

public sealed record CreateHoldRequest(Guid BuyerId, string IdempotencyKey, string[] SeatCodes);
public sealed record ConfirmOrderRequest(Guid HoldId, Guid BuyerId, string IdempotencyKey);

// Phase 1 deliberately has no authentication or payment integration.
// Never deploy publicly until Keycloak and authorization are implemented.
public partial class Program { }
