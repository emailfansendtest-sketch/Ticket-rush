using Microsoft.EntityFrameworkCore;
using TicketRush.Catalog;
using TicketRush.Catalog.Storage;

namespace TicketRush.Catalog.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("CatalogDb")
                ?? throw new InvalidOperationException("CatalogDb connection string is missing.")));
        builder.Services.AddScoped<IEventCatalog, EfEventCatalog>();
        builder.Services.AddHealthChecks();
        var app = builder.Build();
        app.MapHealthChecks("/health/live");
        app.MapGet("/events", async (IEventCatalog catalog, CancellationToken ct) =>
            await catalog.ListEventsAsync(ct));
        app.Run();
    }
}
