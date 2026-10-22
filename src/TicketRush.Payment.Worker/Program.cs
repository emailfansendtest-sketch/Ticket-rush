using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Payment.Persistence;
using TicketRush.Payment.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException("ConnectionStrings:PaymentDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
builder.Services.AddDbContext<PaymentDbContext>(x => x.UseNpgsql(connection));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PaymentAuthorizationProcessor>();
builder.Services.AddScoped<PaymentVoidProcessor>();
builder.Services.AddScoped<PaymentOutboxDispatcher>();
builder.Services.AddSingleton<IPaymentResultPublisher, RabbitMqPaymentResultPublisher>();
builder.Services.AddHostedService<PaymentCommandWorker>();
builder.Services.AddHostedService<PaymentVoidWorker>();
builder.Services.AddHostedService<PaymentOutboxWorker>();
await builder.Build().RunAsync();
