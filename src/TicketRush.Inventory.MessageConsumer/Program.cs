using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Inventory.MessageConsumer;

var builder = Host.CreateApplicationBuilder(args);
var bookingDb = builder.Configuration.GetConnectionString("BookingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:BookingDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
builder.Services.AddDbContext<BookingDbContext>(o => o.UseNpgsql(bookingDb));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<IInventoryCheckout>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<InventorySaleCommandProcessor>();
builder.Services.AddHostedService<InventoryCommandWorker>();
await builder.Build().RunAsync();
