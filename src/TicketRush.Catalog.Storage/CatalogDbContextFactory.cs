using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketRush.Catalog.Storage;

public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CatalogDb")
            ?? "Host=localhost;Port=5440;Database=ticketrush_catalog;Username=ticketrush;Password=localdevonly";
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(connectionString).Options;
        return new CatalogDbContext(options);
    }
}
