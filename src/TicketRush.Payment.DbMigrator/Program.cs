using Microsoft.EntityFrameworkCore;
using TicketRush.Payment.Persistence;

var connection = Environment.GetEnvironmentVariable("ConnectionStrings__PaymentDb")
    ?? throw new InvalidOperationException("ConnectionStrings__PaymentDb must be provided.");
var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(connection).Options;
await using var db = new PaymentDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("Payment migrations applied. No payment requests were created.");
