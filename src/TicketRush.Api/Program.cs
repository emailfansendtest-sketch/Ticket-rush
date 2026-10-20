using Microsoft.EntityFrameworkCore;
using TicketRush.Api.Endpoints;
using TicketRush.Catalog;
using TicketRush.Infrastructure;
using TicketRush.Inventory;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
        ?? throw new InvalidOperationException("BookingDb connection string is missing.")));
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<IEventCatalog, EfEventCatalog>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health/live");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();

app.MapPost("/orders", async (ConfirmOrderRequest request,
    BookingService booking, CancellationToken ct) =>
{
    var result = await booking.ConfirmAsync(request.HoldId, request.BuyerId,
        request.IdempotencyKey, ct);
    return BookingResultMapper.MapResult(result, v => $"/orders/{v.Id}");
});

app.Run();

public sealed record ConfirmOrderRequest(Guid HoldId, Guid BuyerId, string IdempotencyKey);

// Phase 1 deliberately has no authentication or payment integration.
// Never deploy publicly until Keycloak and authorization are implemented.
public partial class Program { }
