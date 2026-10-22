using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketRush.Payment.Persistence;

public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__PaymentDb")
            ?? "Host=localhost;Port=5442;Database=ticketrush_payment;Username=ticketrush;Password=localdevonly";
        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseNpgsql(connection).Options;
        return new PaymentDbContext(options);
    }
}
