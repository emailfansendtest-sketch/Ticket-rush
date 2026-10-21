using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TicketRush.Ordering.OutboxPublisher;
using TicketRush.Ordering.Persistence;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
var orderingDb = builder.Configuration.GetConnectionString("OrderingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrderingDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");

builder.Services.AddDbContext<OrderingDbContext>(options => options.UseNpgsql(orderingDb));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddSingleton<IOutboxPublisher, RabbitMqOutboxPublisher>();
builder.Services.AddHostedService<OutboxWorker>();

await builder.Build().RunAsync();
