using TicketRush.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Inventory.MessageConsumer;

var builder = Host.CreateApplicationBuilder(args);
var inventoryDb = builder.Configuration.GetConnectionString("InventoryDb")
    ?? throw new InvalidOperationException("ConnectionStrings:InventoryDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
builder.Services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(inventoryDb));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<IInventoryCheckout>(sp => sp.GetRequiredService<InventoryService>());
builder.Services.AddScoped<InventorySaleCommandProcessor>();
builder.Services.AddHostedService<InventoryCommandWorker>();
await builder.Build().RunAsync();
