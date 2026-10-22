using TicketRush.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Infrastructure;
using TicketRush.Inventory.ResultPublisher;

var builder = Host.CreateApplicationBuilder(args);
var inventoryDb = builder.Configuration.GetConnectionString("InventoryDb")
    ?? throw new InvalidOperationException("ConnectionStrings:InventoryDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");

builder.Services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(inventoryDb));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InventoryResultOutboxDispatcher>();
builder.Services.AddSingleton<IInventoryResultPublisher, RabbitMqInventoryResultPublisher>();
builder.Services.AddHostedService<InventoryResultOutboxWorker>();
await builder.Build().RunAsync();
