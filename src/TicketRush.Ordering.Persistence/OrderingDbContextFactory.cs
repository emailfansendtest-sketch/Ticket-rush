using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketRush.Ordering.Persistence;

public sealed class OrderingDbContextFactory : IDesignTimeDbContextFactory<OrderingDbContext>
{
    public OrderingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OrderingDb")
            ?? "Host=localhost;Port=5441;Database=ticketrush_ordering;Username=ticketrush;Password=localdevonly";
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(connectionString).Options;
        return new OrderingDbContext(options);
    }
}
