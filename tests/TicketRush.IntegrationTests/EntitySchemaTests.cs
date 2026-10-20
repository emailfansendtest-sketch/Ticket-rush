using Microsoft.EntityFrameworkCore;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.EntitySchemas;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class EntitySchemaTests
{
    // This test does not connect to PostgreSQL. It checks the actual EF/Npgsql
    // model used by BookingService and detects schema-name drift before runtime.
    [Fact]
    public void Schema_names_are_derived_from_ef_core_model()
    {
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new BookingDbContext(options);

        IEntitySchema<SeatAllocation> seats = db.GetEntitySchema<SeatAllocation>();
        Assert.Equal("\"seats\"", seats.Table);
        Assert.Equal("\"event_id\"", seats.Column(x => x.EventId));
        Assert.Equal("\"seat_code\"", seats.Column(x => x.SeatCode));
        Assert.Equal("\"hold_id\"", seats.Column(x => x.HoldId));
        Assert.Contains("\"xmin\"", seats.Columns);

        IEntitySchema<SeatHold> holds = db.GetEntitySchema<SeatHold>();
        Assert.Equal("\"holds\"", holds.Table);
        Assert.Equal("\"id\"", holds.Column(x => x.Id));
        Assert.Contains("\"seat_codes_csv\"", holds.Columns);

        IEntitySchema<TicketEvent> events = db.GetEntitySchema<TicketEvent>();
        Assert.Equal("\"events\"", events.Table);
        Assert.Equal("\"starts_at_utc\"", events.Column(x => x.StartsAtUtc));

        IEntitySchema<TicketOrder> orders = db.GetEntitySchema<TicketOrder>();
        Assert.Equal("\"orders\"", orders.Table);
        Assert.Equal("\"hold_id\"", orders.Column(x => x.HoldId));
    }

    [Fact]
    public void Schema_rejects_computed_expressions_instead_of_treating_them_as_identifiers()
    {
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new BookingDbContext(options);
        var seats = db.GetEntitySchema<SeatAllocation>();
        Assert.Throws<ArgumentException>(() => seats.Column(x => x.SeatCode.ToUpperInvariant()));
    }
}
