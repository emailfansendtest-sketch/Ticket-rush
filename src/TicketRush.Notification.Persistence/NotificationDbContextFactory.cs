using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketRush.Notification.Persistence;

public sealed class NotificationDbContextFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__NotificationDb")
            ?? "Host=localhost;Port=5443;Database=ticketrush_notification;Username=ticketrush;Password=localdevonly";
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(connection).Options;
        return new NotificationDbContext(options);
    }
}
