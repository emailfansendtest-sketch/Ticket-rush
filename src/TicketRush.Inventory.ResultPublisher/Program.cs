using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Infrastructure;
using TicketRush.Inventory.ResultPublisher;

var builder = Host.CreateApplicationBuilder(args);
var bookingDb = builder.Configuration.GetConnectionString("BookingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:BookingDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");

builder.Services.AddDbContext<BookingDbContext>(options => options.UseNpgsql(bookingDb));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<InventoryResultOutboxDispatcher>();
builder.Services.AddSingleton<IInventoryResultPublisher, RabbitMqInventoryResultPublisher>();
builder.Services.AddHostedService<InventoryResultOutboxWorker>();
await builder.Build().RunAsync();
