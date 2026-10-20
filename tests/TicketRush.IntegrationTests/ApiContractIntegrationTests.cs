using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using TicketRush.Catalog;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>
/// Exercises the real Minimal API entry point, DI, routing and JSON serialization
/// against the same real-PostgreSQL migrations used by the service-level tests.
/// The API itself is hosted in-process by TestServer; PostgreSQL is in Testcontainers.
/// </summary>
public sealed class ApiContractIntegrationTests
{
    [Fact]
    public async Task Http_booking_flow_preserves_routes_status_codes_and_idempotent_replies()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        string connectionString = postgres.GetConnectionString();
        await InitializeDatabaseAsync(connectionString);

        using var factory = new TicketRushApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var healthResponse = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        using var eventsResponse = await client.GetAsync("/events");
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        var events = Assert.IsType<EventSummary[]>(
            await eventsResponse.Content.ReadFromJsonAsync<EventSummary[]>());
        Assert.Contains(events, e => e.Id == DemoDataSeeder.DemoEventId);

        string seatUrl = $"/events/{DemoDataSeeder.DemoEventId}/seats";
        using var seatsBeforeResponse = await client.GetAsync(seatUrl);
        Assert.Equal(HttpStatusCode.OK, seatsBeforeResponse.StatusCode);
        var seatsBefore = Assert.IsType<SeatSummary[]>(
            await seatsBeforeResponse.Content.ReadFromJsonAsync<SeatSummary[]>());
        Assert.Equal(100, seatsBefore.Length);
        Assert.All(seatsBefore, s => Assert.Equal(SeatStatus.Available, s.Status));

        var buyer = Guid.NewGuid();
        string holdUrl = $"/events/{DemoDataSeeder.DemoEventId}/holds";
        var holdCommand = new
        {
            buyerId = buyer,
            idempotencyKey = "http-hold-1",
            seatCodes = new[] { "A-001", "A-002" }
        };

        using var holdCreatedResponse = await client.PostAsJsonAsync(holdUrl, holdCommand);
        Assert.Equal(HttpStatusCode.Created, holdCreatedResponse.StatusCode);
        var createdHold = Assert.IsType<HoldView>(
            await holdCreatedResponse.Content.ReadFromJsonAsync<HoldView>());
        Assert.Equal($"/holds/{createdHold.Id:D}?buyerId={buyer:D}",
            holdCreatedResponse.Headers.Location?.OriginalString);

        // Location must resolve to a real resource through a GET request.
        using var readHold = await client.GetAsync(holdCreatedResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, readHold.StatusCode);
        var fetchedHold = Assert.IsType<HoldView>(
            await readHold.Content.ReadFromJsonAsync<HoldView>());
        Assert.Equal(createdHold.Id, fetchedHold.Id);
        Assert.Equal(createdHold.BuyerId, fetchedHold.BuyerId);
        Assert.Equal(createdHold.ExpiresAtUtc, fetchedHold.ExpiresAtUtc);
        Assert.Equal(createdHold.SeatCodes, fetchedHold.SeatCodes);

        using var otherBuyerHoldRead = await client.GetAsync(
            $"/holds/{createdHold.Id:D}?buyerId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, otherBuyerHoldRead.StatusCode);
        using var missingBuyerHoldRead = await client.GetAsync($"/holds/{createdHold.Id:D}");
        Assert.Equal(HttpStatusCode.BadRequest, missingBuyerHoldRead.StatusCode);
        Assert.Equal(buyer, createdHold.BuyerId);
        Assert.Equal(new[] { "A-001", "A-002" }, createdHold.SeatCodes);

        using var holdReplayedResponse = await client.PostAsJsonAsync(holdUrl, holdCommand);
        Assert.Equal(HttpStatusCode.OK, holdReplayedResponse.StatusCode);
        var replayedHold = Assert.IsType<HoldView>(
            await holdReplayedResponse.Content.ReadFromJsonAsync<HoldView>());
        Assert.Equal(createdHold.Id, replayedHold.Id);
        Assert.Equal(createdHold.ExpiresAtUtc, replayedHold.ExpiresAtUtc);
        Assert.Equal(createdHold.SeatCodes, replayedHold.SeatCodes);

        using var otherBuyerResponse = await client.PostAsJsonAsync(holdUrl, new
        {
            buyerId = Guid.NewGuid(),
            idempotencyKey = "different-buyer-key",
            seatCodes = new[] { "A-001" }
        });
        Assert.Equal(HttpStatusCode.Conflict, otherBuyerResponse.StatusCode);

        var checkoutCommand = new
        {
            holdId = createdHold.Id,
            buyerId = buyer,
            idempotencyKey = "http-checkout-1"
        };
        using var orderCreatedResponse = await client.PostAsJsonAsync("/orders", checkoutCommand);
        Assert.Equal(HttpStatusCode.Created, orderCreatedResponse.StatusCode);
        var createdOrder = Assert.IsType<OrderView>(
            await orderCreatedResponse.Content.ReadFromJsonAsync<OrderView>());
        Assert.Equal($"/orders/{createdOrder.Id:D}?buyerId={buyer:D}",
            orderCreatedResponse.Headers.Location?.OriginalString);

        using var readOrder = await client.GetAsync(orderCreatedResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, readOrder.StatusCode);
        Assert.Equal(createdOrder,
            await readOrder.Content.ReadFromJsonAsync<OrderView>());

        using var otherBuyerOrderRead = await client.GetAsync(
            $"/orders/{createdOrder.Id:D}?buyerId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, otherBuyerOrderRead.StatusCode);
        using var missingBuyerOrderRead = await client.GetAsync($"/orders/{createdOrder.Id:D}");
        Assert.Equal(HttpStatusCode.BadRequest, missingBuyerOrderRead.StatusCode);
        Assert.Equal(createdHold.Id, createdOrder.HoldId);

        using var orderReplayedResponse = await client.PostAsJsonAsync("/orders", checkoutCommand);
        Assert.Equal(HttpStatusCode.OK, orderReplayedResponse.StatusCode);
        var replayedOrder = Assert.IsType<OrderView>(
            await orderReplayedResponse.Content.ReadFromJsonAsync<OrderView>());
        Assert.Equal(createdOrder, replayedOrder);

        using var seatsAfterResponse = await client.GetAsync(seatUrl);
        Assert.Equal(HttpStatusCode.OK, seatsAfterResponse.StatusCode);
        var seatsAfter = Assert.IsType<SeatSummary[]>(
            await seatsAfterResponse.Content.ReadFromJsonAsync<SeatSummary[]>());
        Assert.Equal(SeatStatus.Sold, Assert.Single(seatsAfter, s => s.SeatCode == "A-001").Status);
        Assert.Equal(SeatStatus.Sold, Assert.Single(seatsAfter, s => s.SeatCode == "A-002").Status);
        Assert.Equal(SeatStatus.Available,
            Assert.Single(seatsAfter, s => s.SeatCode == "A-003").Status);

        var options = CreateDbOptions(connectionString);
        await using var verify = new BookingDbContext(options);
        Assert.Equal(1, await verify.Holds.CountAsync());
        Assert.Equal(1, await verify.Orders.CountAsync());
    }

    [Fact]
    public async Task Http_invalid_and_missing_seat_requests_do_not_persist_partial_holds()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await InitializeDatabaseAsync(connectionString);

        using var factory = new TicketRushApiFactory(connectionString);
        using var client = factory.CreateClient();
        string url = $"/events/{DemoDataSeeder.DemoEventId}/holds";
        var buyer = Guid.NewGuid();

        using var invalid = await client.PostAsJsonAsync(url, new
        {
            buyerId = buyer,
            idempotencyKey = "invalid-duplicates",
            seatCodes = new[] { "A-005", "A-005" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var absent = await client.PostAsJsonAsync(url, new
        {
            buyerId = buyer,
            idempotencyKey = "missing-seat",
            seatCodes = new[] { "A-005", "A-999" }
        });
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        await using var verify = new BookingDbContext(CreateDbOptions(connectionString));
        Assert.Empty(await verify.Holds.AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.Orders.AsNoTracking().ToArrayAsync());
        var seat = await verify.Seats.AsNoTracking().SingleAsync(s =>
            s.EventId == DemoDataSeeder.DemoEventId && s.SeatCode == "A-005");
        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.Null(seat.HoldId);
        Assert.Null(seat.HeldUntilUtc);
        Assert.Null(seat.SoldOrderId);
    }

    [Fact]
    public async Task Http_expired_hold_is_reported_available_and_can_be_reclaimed()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await InitializeDatabaseAsync(connectionString);

        var clock = new TimeProviderMock
        {
            CurrentTimeUtc = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)
        };
        using var factory = new TicketRushApiFactory(connectionString, clock);
        using var client = factory.CreateClient();
        string seatUrl = $"/events/{DemoDataSeeder.DemoEventId:D}/seats";
        string holdUrl = $"/events/{DemoDataSeeder.DemoEventId:D}/holds";
        Guid firstBuyer = Guid.NewGuid();

        using var firstResponse = await client.PostAsJsonAsync(holdUrl, new
        {
            buyerId = firstBuyer,
            idempotencyKey = "expiration-http-a",
            seatCodes = new[] { "A-050" }
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstHold = Assert.IsType<HoldView>(
            await firstResponse.Content.ReadFromJsonAsync<HoldView>());

        using (var seatsDuringResponse = await client.GetAsync(seatUrl))
        {
            var during = Assert.IsType<SeatSummary[]>(
                await seatsDuringResponse.Content.ReadFromJsonAsync<SeatSummary[]>());
            Assert.Equal(SeatStatus.Held, Assert.Single(during, x => x.SeatCode == "A-050").Status);
        }

        // Exactly at the expiry instant (not one tick later) the seat is available.
        clock.CurrentTimeUtc = firstHold.ExpiresAtUtc;

        using var expiredResponse = await client.GetAsync(firstResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, expiredResponse.StatusCode);
        var expiredHold = Assert.IsType<HoldView>(
            await expiredResponse.Content.ReadFromJsonAsync<HoldView>());
        Assert.Equal("Expired", expiredHold.Status);

        using (var seatsAfterResponse = await client.GetAsync(seatUrl))
        {
            var after = Assert.IsType<SeatSummary[]>(
                await seatsAfterResponse.Content.ReadFromJsonAsync<SeatSummary[]>());
            var seat = Assert.Single(after, x => x.SeatCode == "A-050");
            Assert.Equal(SeatStatus.Available, seat.Status);
            Assert.Null(seat.HeldUntilUtc);
        }

        // Availability reads must not mutate the authoritative rows.
        await using (var verify = new BookingDbContext(CreateDbOptions(connectionString)))
        {
            var stored = await verify.Seats.AsNoTracking().SingleAsync(
                x => x.EventId == DemoDataSeeder.DemoEventId && x.SeatCode == "A-050");
            Assert.Equal(SeatStatus.Held, stored.Status);
            Assert.Equal(firstHold.Id, stored.HoldId);
        }

        using var secondResponse = await client.PostAsJsonAsync(holdUrl, new
        {
            buyerId = Guid.NewGuid(),
            idempotencyKey = "expiration-http-b",
            seatCodes = new[] { "A-050" }
        });
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var secondHold = Assert.IsType<HoldView>(
            await secondResponse.Content.ReadFromJsonAsync<HoldView>());
        Assert.NotEqual(firstHold.Id, secondHold.Id);

        // Replaying an expired idempotent command must not claim that its
        // reservation is still Active, even after another buyer reclaimed it.
        using var expiredReplayResponse = await client.PostAsJsonAsync(holdUrl, new
        {
            buyerId = firstBuyer,
            idempotencyKey = "expiration-http-a",
            seatCodes = new[] { "A-050" }
        });
        Assert.Equal(HttpStatusCode.OK, expiredReplayResponse.StatusCode);
        var expiredReplay = Assert.IsType<HoldView>(
            await expiredReplayResponse.Content.ReadFromJsonAsync<HoldView>());
        Assert.Equal(firstHold.Id, expiredReplay.Id);
        Assert.Equal("Expired", expiredReplay.Status);

        using var seatsReassignedResponse = await client.GetAsync(seatUrl);
        var reassigned = Assert.IsType<SeatSummary[]>(
            await seatsReassignedResponse.Content.ReadFromJsonAsync<SeatSummary[]>());
        Assert.Equal(SeatStatus.Held,
            Assert.Single(reassigned, x => x.SeatCode == "A-050").Status);
    }

    private static DbContextOptions<BookingDbContext> CreateDbOptions(string connectionString) =>
        new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(connectionString).Options;

    private static Task InitializeDatabaseAsync(string connectionString) =>
        TestDatabaseInitializer.InitializeAsync(CreateDbOptions(connectionString));

    private sealed class FixedDemoEventCatalog : IEventCatalog
    {
        public Task<IReadOnlyList<EventSummary>> ListEventsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EventSummary>>(new[]
            {
                new EventSummary(DemoDataSeeder.DemoEventId, "Demo Concert",
                    new DateTimeOffset(2027, 4, 20, 18, 0, 0, TimeSpan.Zero))
            });
    }

    private sealed class TicketRushApiFactory(
        string connectionString, TimeProvider? testClock = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // The solution uses src/TicketRush.Api, not the default <solution>/TicketRush.Api.
            // Resolve the actual content root explicitly so WebApplicationFactory
            // does not depend on its solution-relative folder convention.
            builder.UseContentRoot(FindApiContentRoot());

            // Existing booking HTTP tests use an isolated stub Catalog, so the
            // known Phase 1 booking behavior does not depend on an external host.
            // Cross-service forwarding is tested separately by CatalogServiceHttpTests.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventCatalog>();
                services.AddSingleton<IEventCatalog>(new FixedDemoEventCatalog());
            });

            var clockOverride = testClock;
            if (clockOverride is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clockOverride);
                });
            }

            // API Program.cs uses builder.Configuration inside the DbContext options
            // factory, so this override is observed at dependency resolution time.
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:BookingDb"] = connectionString
                }));
        }

        private static string FindApiContentRoot()
        {
            for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
                folder is not null; folder = folder.Parent)
            {
                if (!File.Exists(Path.Combine(folder.FullName, "TicketRush.slnx")))
                    continue;

                string path = Path.Combine(folder.FullName, "src", "TicketRush.Api");
                if (File.Exists(Path.Combine(path, "TicketRush.Api.csproj")))
                    return path;
            }

            throw new InvalidOperationException(
                "Could not locate src/TicketRush.Api relative to TicketRush.slnx.");
        }
    }
}
