using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Catalog;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class CatalogIntegrationTests
{
    [Fact]
    public async Task Event_listing_returns_chronological_untracked_projection()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;

        await TestDatabaseInitializer.InitializeAsync(options);

        Guid earlierId = Guid.NewGuid();
        await using (var setup = new BookingDbContext(options))
        {
            setup.Events.Add(new TicketEvent
            {
                Id = earlierId,
                Name = "Earlier concert",
                StartsAtUtc = new DateTimeOffset(2027, 4, 19, 18, 0, 0, TimeSpan.Zero)
            });
            await setup.SaveChangesAsync();
        }

        await using var db = new BookingDbContext(options);
        IEventCatalog catalog = new EfEventCatalog(db);
        var events = await catalog.ListEventsAsync(CancellationToken.None);

        Assert.Equal(2, events.Count);
        Assert.Equal(earlierId, events[0].Id);
        Assert.Equal("Earlier concert", events[0].Name);
        Assert.Equal(DemoDataSeeder.DemoEventId, events[1].Id);
        Assert.Equal("Demo Concert", events[1].Name);
        Assert.Empty(db.ChangeTracker.Entries<TicketEvent>());
    }

    [Fact]
    public void Shared_infrastructure_must_not_depend_on_catalog_module()
    {
        var references = typeof(BookingDbContext).Assembly.GetReferencedAssemblies();
        string catalogAssemblyName = typeof(IEventCatalog).Assembly.GetName().Name!;
        Assert.DoesNotContain(references, assembly => assembly.Name == catalogAssemblyName);
    }
}
