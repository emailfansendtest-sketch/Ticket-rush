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
// These settings are operational safety limits. Replays retain original MessageId.
static int ReadPositive(Microsoft.Extensions.Configuration.IConfiguration config,
    string key, int fallback)
{
    var value = config[key];
    if (value is null) return fallback;
    if (!int.TryParse(value, out var parsed) || parsed <= 0)
        throw new InvalidOperationException($"{key} must be a positive integer.");
    return parsed;
}
var recoveryOptions = new SagaRecoveryOptions(
    TimeSpan.FromSeconds(ReadPositive(builder.Configuration, "SagaRecovery:InitialTimeoutSeconds", 120)),
    TimeSpan.FromSeconds(ReadPositive(builder.Configuration, "SagaRecovery:RetryBaseSeconds", 30)),
    TimeSpan.FromSeconds(ReadPositive(builder.Configuration, "SagaRecovery:RetryMaxSeconds", 600)),
    ReadPositive(builder.Configuration, "SagaRecovery:MaxAttempts", 5));
recoveryOptions.Validate();
builder.Services.AddSingleton(recoveryOptions);
builder.Services.AddScoped<SagaRecoveryDispatcher>();
builder.Services.AddHostedService<SagaRecoveryWorker>();
builder.Services.AddHostedService<CheckoutRequestedWorker>();
builder.Services.AddHostedService<InventoryResultWorker>();
builder.Services.AddHostedService<PaymentResultWorker>();
await builder.Build().RunAsync();
