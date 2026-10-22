using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Notification.Persistence;
using TicketRush.Notification.Worker;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("NotificationDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NotificationDb is required.");
_ = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NotificationDeliveryProcessor>();
builder.Services.AddHostedService<NotificationWorker>();
await builder.Build().RunAsync();
