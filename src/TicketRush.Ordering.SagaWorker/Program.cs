using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Ordering.Persistence;
using TicketRush.Ordering.SagaWorker;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("OrderingDb")
    ?? throw new InvalidOperationException("ConnectionStrings:OrderingDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
builder.Services.AddDbContext<OrderingDbContext>(x => x.UseNpgsql(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OrderingSagaProcessor>();
builder.Services.AddHostedService<CheckoutRequestedWorker>();
builder.Services.AddHostedService<InventoryResultWorker>();
await builder.Build().RunAsync();
