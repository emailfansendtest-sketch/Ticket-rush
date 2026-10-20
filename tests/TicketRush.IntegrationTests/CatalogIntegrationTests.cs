using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Catalog;
using TicketRush.Catalog.Storage;
using TicketRush.Infrastructure;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class CatalogIntegrationTests
{
    [Fact]
    public async Task Event_listing_returns_chronological_untracked_projection_from_catalog_owned_database()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using (var setup = new CatalogDbContext(options))
        {
            await setup.Database.MigrateAsync();
            await CatalogDemoDataSeeder.SeedAsync(setup);
            setup.Events.Add(new CatalogEvent
            {
                Id = Guid.NewGuid(),
                Name = "Earlier concert",
                StartsAtUtc = new DateTimeOffset(2027, 4, 19, 18, 0, 0, TimeSpan.Zero)
            });
            await setup.SaveChangesAsync();
        }

        await using var db = new CatalogDbContext(options);
        IEventCatalog catalog = new EfEventCatalog(db);
        var events = await catalog.ListEventsAsync(CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.Equal("Earlier concert", events[0].Name);
        Assert.Equal(CatalogDemoDataSeeder.DemoEventId, events[1].Id);
        Assert.Empty(db.ChangeTracker.Entries<CatalogEvent>());
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Catalog_storage_and_contracts_must_not_depend_on_booking_infrastructure_or_domain()
    {
        var forbidden = new[]
        {
            typeof(BookingDbContext).Assembly.GetName().Name,
            typeof(TicketRush.Domain.TicketEvent).Assembly.GetName().Name
        };
        foreach (var assembly in new[]
        {
            typeof(CatalogDbContext).Assembly,
            typeof(IEventCatalog).Assembly
        })
        {
            var references = assembly.GetReferencedAssemblies();
            Assert.DoesNotContain(references, r => forbidden.Contains(r.Name));
        }
        // The Booking persistence assembly must not depend on Catalog's DTOs/storage.
        var bookingReferences = typeof(BookingDbContext).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(bookingReferences,
            r => r.Name == typeof(CatalogDbContext).Assembly.GetName().Name);
        Assert.DoesNotContain(bookingReferences,
            r => r.Name == typeof(IEventCatalog).Assembly.GetName().Name);
    }
}
