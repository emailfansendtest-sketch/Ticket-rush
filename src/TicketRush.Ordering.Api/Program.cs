using Microsoft.EntityFrameworkCore;
using TicketRush.BuildingBlocks.Contracts.Security;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.Api;

// Independent API: only Ordering storage is referenced. No direct Booking
// database access and no transaction shared with Inventory.
public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<OrderingDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("OrderingDb")
                ?? throw new InvalidOperationException("OrderingDb connection string is missing.")));
        builder.Services.AddScoped<CheckoutIntakeService>();
        builder.Services.AddScoped<CheckoutStatusReader>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<HoldCheckoutProof>(services => new HoldCheckoutProof(
            services.GetRequiredService<IConfiguration>()["HoldProof:KeyBase64"]));
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        // Validate the final configuration at startup, including test-host overrides.
        _ = app.Services.GetRequiredService<HoldCheckoutProof>();
        app.MapHealthChecks("/health/live");
        app.MapCheckoutEndpoints();
        app.Run();
    }
}
