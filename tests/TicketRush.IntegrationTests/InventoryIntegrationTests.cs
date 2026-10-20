using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class InventoryIntegrationTests
{
    [Fact]
    public async Task Seat_listing_returns_sorted_untracked_projection_with_current_hold_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        await using (var write = new BookingDbContext(options))
        {
            IInventoryService inventory = new InventoryService(write, TimeProvider.System);
            var result = await inventory.HoldAsync(DemoDataSeeder.DemoEventId,
                Guid.NewGuid(), "inventory-projection-test", ["A-002"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            Assert.NotNull(result.Value);
        }

        await using var read = new BookingDbContext(options);
        IInventoryService reader = new InventoryService(read, TimeProvider.System);
        var seats = await reader.ListSeatsAsync(DemoDataSeeder.DemoEventId, CancellationToken.None);

        Assert.Equal(100, seats.Count);
        Assert.Equal("A-001", seats[0].SeatCode);
        Assert.Equal("A-100", seats[^1].SeatCode);
        var held = Assert.Single(seats, s => s.SeatCode == "A-002");
        Assert.Equal(SeatStatus.Held, held.Status);
        Assert.NotNull(held.HeldUntilUtc);
        Assert.Equal(99, seats.Count(s => s.Status == SeatStatus.Available));
        Assert.Empty(read.ChangeTracker.Entries<SeatAllocation>());
    }

    [Fact]
    public void Infrastructure_must_not_depend_on_inventory_module()
    {
        var references = typeof(BookingDbContext).Assembly.GetReferencedAssemblies();
        string name = typeof(IInventoryService).Assembly.GetName().Name!;
        Assert.DoesNotContain(references, assembly => assembly.Name == name);
    }
}
