using Microsoft.EntityFrameworkCore;
using TicketRush.Api.Endpoints;
using TicketRush.Catalog;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Ordering;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("BookingDb")
        ?? throw new InvalidOperationException("BookingDb connection string is missing.")));

// Catalog is independently hosted. Never open its database from the Booking API.
builder.Services.AddHttpClient<IEventCatalog, HttpEventCatalog>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"]
        ?? "http://localhost:8090/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
// Two Inventory ports resolve to ONE scoped instance and shared EF context.
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<IInventoryService>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<IInventoryCheckout>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<IOrderingService, OrderingService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health/live");

app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrderingEndpoints();

app.Run();

// Phase 1 deliberately has no authentication or payment integration.
// Never deploy publicly until Keycloak and authorization are implemented.
public partial class Program { }
