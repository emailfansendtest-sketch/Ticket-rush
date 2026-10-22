using Microsoft.EntityFrameworkCore;
using TicketRush.Notification.Persistence;

var connection = Environment.GetEnvironmentVariable("ConnectionStrings__NotificationDb")
    ?? throw new InvalidOperationException("ConnectionStrings__NotificationDb must be provided.");
var options = new DbContextOptionsBuilder<NotificationDbContext>()
    .UseNpgsql(connection).Options;
await using var db = new NotificationDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("Notification migrations applied. No simulated deliveries created.");
