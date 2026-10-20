using Microsoft.EntityFrameworkCore;
using TicketRush.Ordering.Persistence;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OrderingDb")
    ?? throw new InvalidOperationException("ConnectionStrings__OrderingDb must be provided.");
var options = new DbContextOptionsBuilder<OrderingDbContext>()
    .UseNpgsql(connectionString).Options;
await using var db = new OrderingDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("Ordering migrations applied. No demo orders were created.");
